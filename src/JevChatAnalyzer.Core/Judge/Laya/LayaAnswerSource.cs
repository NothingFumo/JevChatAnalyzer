using System.Text.Json.Nodes;
using JevChatAnalyzer.Core.Messages;
using NLaya;
using NLaya.TorchSharp;

namespace JevChatAnalyzer.Core.Judge.Laya;

/// <summary>
///     答案来源：state + 题目 → 答案。真实实现包 LayaAgent（TorchSharp 直载权重），
///     测试替身直接给答案 —— 这是 implementation 私有的 seam，只为验证映射/摘要/重排序而存在。
/// </summary>
internal interface IAnswerSource : IAsyncDisposable
{
    /// <summary>模型标识（写进 JudgeVerdict.Model）。</summary>
    string ModelId { get; }

    Task<IReadOnlyDictionary<string, JudgeAnswer>> AnswerAsync(JsonObject state,
        IReadOnlyList<QuestionSpec> specs, CancellationToken ct);

    /// <summary>预加载引擎（失败不抛出，留给首次判读重试）。</summary>
    Task WarmupAsync(CancellationToken ct);
}

/// <summary>
///     进程内 laya 引擎 adapter：懒加载 LayaAgent（约 1-2 秒），失败不缓存（下次判读重试），
///     推理串行化并在线程池执行（不阻塞 UI）。
/// </summary>
internal sealed class LayaAnswerSource(LayaModel model) : IAnswerSource
{
    private const string DefaultModelId = "laya-multilingual";

    private readonly object _lock = new();
    private readonly SemaphoreSlim _predictGate = new(1, 1);
    private LayaAgent? _agent;
    private bool _disposed;
    private Task<LayaAgent>? _loading;

    public string ModelId => _agent?.ModelId ?? DefaultModelId;

    public async Task<IReadOnlyDictionary<string, JudgeAnswer>> AnswerAsync(JsonObject state,
        IReadOnlyList<QuestionSpec> specs, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct).ConfigureAwait(false);
        var note = state.ContainsKey("背景") ? JudgeProtocol.BackgroundNote : "";
        var questions = BuildQuestions(specs, note);
        var stateJson = state.ToJsonString();
        var result = await Task.Run(() => Predict(agent, stateJson, questions, ct), ct).ConfigureAwait(false);
        return ToAnswers(result, specs);
    }

    public async Task WarmupAsync(CancellationToken ct)
    {
        _ = await GetAgentAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        LayaAgent? agent;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            agent = _agent;
            _agent = null;
            _loading = null;
        }

        if (agent is null)
        {
            _predictGate.Dispose();
            return;
        }

        // 排空在飞的推理再释放权重：libtorch 仍在读张量时释放会 use-after-free（退出即崩溃）
        _predictGate.Wait();
        try
        {
            await agent.DisposeAsync();
        }
        finally
        {
            _predictGate.Release();
            _predictGate.Dispose();
        }
    }

    private Task<LayaAgent> GetAgentAsync(CancellationToken ct)
    {
        Task<LayaAgent> task;
        lock (_lock)
        {
            task = _loading ??= NLaya.Laya.LoadAsync(model.Directory, o => o.UseTorchSharp());
        }

        return AwaitAsync(task, ct);
    }

    private async Task<LayaAgent> AwaitAsync(Task<LayaAgent> task, CancellationToken ct)
    {
        try
        {
            var agent = await task.WaitAsync(ct).ConfigureAwait(false);
            _agent = agent;
            return agent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 取消不是失败：在途加载保留，下一次判读直接复用
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_loading, task)) _loading = null; // 失败不缓存：模型可能刚下载完，下次判读重试
            }

            throw;
        }
    }

    private LayaResult Predict(LayaAgent agent, string stateJson, Questions questions, CancellationToken ct)
    {
        _predictGate.Wait(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return agent.Predict(stateJson, questions, new PredictOptions());
        }
        finally
        {
            _predictGate.Release();
        }
    }

    private static Questions BuildQuestions(IReadOnlyList<QuestionSpec> specs, string note)
    {
        var questions = new Questions();
        foreach (var spec in specs)
        {
            var instructions = spec.Instructions + note;
            questions = spec.Kind switch
            {
                "choice" => questions.Choice(spec.Id, instructions,
                    spec.Options!.Select(o => (o.Label, (string?)o.Description))),
                "score" => questions.Score(spec.Id, instructions, spec.Levels!),
                _ => questions.Noul(spec.Id, instructions, b =>
                {
                    if (spec.Noul is { } criteria) b.WhenTrue(criteria.True).WhenFalse(criteria.False);
                })
            };
        }

        return questions;
    }

    private static IReadOnlyDictionary<string, JudgeAnswer> ToAnswers(LayaResult result,
        IReadOnlyList<QuestionSpec> specs)
    {
        var answers = new Dictionary<string, JudgeAnswer>();
        foreach (var spec in specs)
        {
            if (!result.TryGet(spec.Id, out var answer) || answer is null) continue;
            answers[spec.Id] = spec.Kind switch
            {
                "choice" => new JudgeAnswer("choice", result.Choice(spec.Id).Choice,
                    0, result.Choice(spec.Id).Confidence,
                    new Dictionary<string, double>(result.Choice(spec.Id).Probabilities)),
                "score" => new JudgeAnswer("score", null, result.Score(spec.Id).Score,
                    result.Score(spec.Id).Confidence),
                _ => Noul(result, spec.Id)
            };
        }

        return answers;
    }

    private static JudgeAnswer Noul(LayaResult result, string id)
    {
        var probability = result.Noul(id).Probability;
        return new JudgeAnswer("noul", probability >= 0.5 ? "是" : "否", probability,
            Math.Max(probability, 1 - probability));
    }
}
