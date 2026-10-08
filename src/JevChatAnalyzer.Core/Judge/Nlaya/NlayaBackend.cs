using JevChatAnalyzer.Core.Messages;
using NLaya;
using NLaya.TorchSharp;

namespace JevChatAnalyzer.Core.Judge.NlayaJudge;

/// <summary>
/// NLaya 进程内判读后端：TorchSharp 直载 HF safetensors 权重（multilingual checkpoint），
/// 无需 Python/ONNX 导出，离线可用
/// 引擎懒加载（首次判读时载入，约 1-2 秒），进程内共享单实例。
/// </summary>
public sealed class NlayaBackend : IJudgeBackend
{
    private readonly string? _modelDir;
    private readonly object _lock = new();
    private LayaAgent? _agent;
    private Task<LayaAgent>? _loading;

    public NlayaBackend(string? modelDir = null)
    {
        _modelDir = modelDir;
    }

    public string Name => "laya 进程内";

    public string Model => "laya-multilingual";

    /// <summary>模型文件是否就绪（设置页状态显示）。</summary>
    public bool IsModelDownloaded => NlayaModelDownloader.IsModelReady(_modelDir);

    private LayaAgent Agent
    {
        get
        {
            lock (_lock)
            {
                if (_agent is not null)
                {
                    return _agent;
                }
                _loading ??= Laya.LoadAsync(_modelDir ?? NlayaModelDownloader.DefaultDirectory(),
                    o => o.UseTorchSharp());
                _agent = _loading.GetAwaiter().GetResult();
                return _agent;
            }
        }
    }

    /// <summary>预加载引擎（应用启动后可后台调用，消除首次判读延迟）。</summary>
    public Task WarmupAsync(CancellationToken ct = default) => Task.Run(() => _ = Agent, ct);

    public Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct)
    {
        var agent = Agent;
        var questions = BuildQuestions();
        var stateJson = JudgeQuestions.BuildState(request).ToJsonString();
        var result = agent.Predict(stateJson, questions, new PredictOptions());

        var answers = new Dictionary<string, JudgeAnswer>();
        foreach (var spec in JudgeQuestions.Specs)
        {
            if (!result.TryGet(spec.Id, out var answer) || answer is null)
            {
                continue;
            }
            answers[spec.Id] = spec.Kind switch
            {
                "choice" => new JudgeAnswer("choice", result.Choice(spec.Id).Choice,
                    0, result.Choice(spec.Id).Confidence),
                "score" => new JudgeAnswer("score", null, result.Score(spec.Id).Score,
                    result.Score(spec.Id).Confidence),
                _ => HandleNoul(result, spec.Id),
            };
        }
        return Task.FromResult(new JudgeVerdict
        {
            BackendName = "nlaya",
            Model = agent.ModelId,
            JudgedAt = DateTimeOffset.Now,
            Answers = answers,
            Summary = HttpBackend.BuildSummary(answers),
        });
    }

    private static JudgeAnswer HandleNoul(LayaResult result, string id)
    {
        var noul = result.Noul(id).Probability;
        return new JudgeAnswer("noul", noul >= 0.5 ? "是" : "否", noul, Math.Max(noul, 1 - noul));
    }

    /// <summary>按七题口径构建 NLaya Questions。</summary>
    internal static Questions BuildQuestions()
    {
        var questions = new Questions();
        foreach (var spec in JudgeQuestions.Specs)
        {
            switch (spec.Kind)
            {
                case "choice":
                    questions = questions.Choice(spec.Id, spec.Instructions,
                        spec.Options!.Select(o => (o.Label, o.Description)));
                    break;
                case "score":
                    questions = questions.Score(spec.Id, spec.Instructions, spec.Levels!);
                    break;
                default:
                    questions = questions.Noul(spec.Id, spec.Instructions, _ => { });
                    break;
            }
        }
        return questions;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(IsModelDownloaded);

    public async ValueTask DisposeAsync()
    {
        LayaAgent? agent;
        lock (_lock)
        {
            agent = _agent;
            _agent = null;
            _loading = null;
        }
        if (agent is not null)
        {
            await agent.DisposeAsync();
        }
    }
}
