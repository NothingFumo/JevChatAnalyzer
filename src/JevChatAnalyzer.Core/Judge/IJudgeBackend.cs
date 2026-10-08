using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Judge;

/// <summary>判读后端抽象：线协议（HTTP）与进程内 laya 共用</summary>
public interface IJudgeBackend : IAsyncDisposable
{
    /// <summary>后端显示名（如「云端服务 云端」「laya 本地」）。</summary>
    string Name { get; }

    /// <summary>模型/端点标识（写进 JudgeVerdict.Model）。</summary>
    string Model { get; }

    /// <summary>执行一次判读。实现自行处理超时；异常即视为本次失败（交给上层熔断）。</summary>
    Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken ct);

    /// <summary>
    /// 通用单题组问答（如候选回复重排序）：state + 任意题目组 → 题目 id → 答案。
    /// 与判读题库解耦，供管线扩展复用后端连接。
    /// </summary>
    Task<IReadOnlyDictionary<string, JudgeAnswer>> AskAsync(object state, IReadOnlyList<QuestionSpec> specs,
        CancellationToken ct);

    /// <summary>可用性探测（轻量，如健康检查或已初始化标记）。</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);
}
