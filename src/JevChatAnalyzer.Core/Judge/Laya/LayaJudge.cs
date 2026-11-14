using System.Text.Json;
using System.Text.Json.Nodes;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Judge.Laya;

/// <summary>
///     本地 laya 判读：唯一判读来源。
///     interface 三件事：判读一条待分析消息、候选回复重排序、模型状态；外加预热与释放。
///     引擎加载、题目构建、答案映射、摘要全部藏在 implementation 里。
/// </summary>
public sealed class LayaJudge : IAsyncDisposable
{
    private readonly IAnswerSource _source;

    public LayaJudge() : this(new LayaModel())
    {
    }

    public LayaJudge(LayaModel model) : this(model, new LayaAnswerSource(model))
    {
    }

    internal LayaJudge(LayaModel model, IAnswerSource source)
    {
        Model = model;
        _source = source;
    }

    /// <summary>模型（下载与就绪状态）。</summary>
    public LayaModel Model { get; }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>模型就绪状态（设置页显示，实查文件）。</summary>
    public LayaModelStatus Status()
    {
        return Model.Status();
    }

    /// <summary>判读一条待分析消息。模型未就绪时抛 <see cref="JudgeException" />（含缺失文件数）。</summary>
    public async Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct = default)
    {
        EnsureReady();
        try
        {
            // 风险题交给确定性扫描：本地 laya 的风险判断不可靠，避免给出“无风险”的假安心。
            var specs = JudgeProtocol.Specs.Where(s => s.Id != JudgeProtocol.RiskId).ToList();
            var answers = await _source
                .AnswerAsync(JudgeProtocol.BuildState(request), specs, ct)
                .ConfigureAwait(false);
            var merged = new Dictionary<string, JudgeAnswer>(answers)
            {
                [JudgeProtocol.RiskId] = RiskScanner.Scan(request.Focus.Text)
            };
            return JudgeProtocol.ToVerdict(merged, _source.ModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new JudgeException($"laya 判读失败：{e.Message}", e);
        }
    }

    /// <summary>
    ///     候选回复重排序（三段式第三段）：一道 choice 题，选项 = 各候选，选中项置顶。
    ///     候选不足两条时原样返回；引擎给不出有效选择时保持原序。
    /// </summary>
    public async Task<IReadOnlyList<DraftSuggestion>> RerankAsync(JudgeRequest request,
        IReadOnlyList<DraftSuggestion> candidates, CancellationToken ct = default)
    {
        if (candidates.Count < 2) return candidates;
        EnsureReady();
        var spec = JudgeProtocol.RerankSpec(candidates.Select(c => c.Text).ToArray());
        var state = JudgeProtocol.BuildState(request);
        state["候选回复"] = JsonNode.Parse(JsonSerializer.Serialize(
            candidates.Select((c, i) => new { id = $"候选{i + 1}", text = c.Text })));
        try
        {
            var answers = await _source.AnswerAsync(state, [spec], ct).ConfigureAwait(false);
            var index = answers.TryGetValue(spec.Id, out var answer)
                ? JudgeProtocol.PickCandidate(answer.Label, candidates.Count)
                : -1;
            if (index < 0) return candidates;
            var ordered = new List<DraftSuggestion> { candidates[index] };
            ordered.AddRange(candidates.Where((_, i) => i != index));
            return ordered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new JudgeException($"候选重排序失败：{e.Message}", e);
        }
    }

    /// <summary>预加载引擎（应用启动时后台调用）：模型未就绪或加载失败都直接返回，首次判读会重试。</summary>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        if (!Model.Status().Ready) return;
        try
        {
            await _source.WarmupAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 预热失败不影响启动；首次判读会重新加载并给出具体错误
        }
    }

    private void EnsureReady()
    {
        var status = Model.Status();
        if (status.Ready) return;
        throw new JudgeException(
            $"laya 模型未就绪（{status.Present}/{status.Total} 文件）：请到 设置 → 本地 laya 下载模型。\n" +
            $"目录：{status.Directory}");
    }
}

/// <summary>判读失败（模型未就绪或引擎异常）。</summary>
public sealed class JudgeException : Exception
{
    public JudgeException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
