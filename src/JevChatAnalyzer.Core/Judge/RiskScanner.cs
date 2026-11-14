using JevChatAnalyzer.Core.Messages;
namespace JevChatAnalyzer.Core.Judge;

/// <summary>
///     风险题的确定性兜底：本地 laya 的 noul/choice 头在风险语义上不可靠
///     （实测“转500保证金”也被判为无风险），改用关键词规则给出可解释结论。
///     只做初筛，不替代人工核实。
/// </summary>
public static class RiskScanner
{
    private static readonly string[] HighKeywords =
    [
        "验证码", "支付密码", "银行卡号", "保证金", "押金", "刷单", "返利",
        "中奖", "退款", "转账", "汇款", "点链接", "点击链接", "扫二维码",
        "收款码", "安全账户", "解冻"
    ];

    private static readonly string[] MediumKeywords =
    [
        "借钱", "账号", "密码", "链接", "http", "投资", "理财", "贷款", "身份证", "客服"
    ];

    public static JudgeAnswer Scan(string? text)
    {
        var t = text ?? "";
        if (HighKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return Answer("高风险", 1.0);
        if (MediumKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return Answer("需留意", 0.5);
        return Answer("无风险", 0.0);
    }

    private static JudgeAnswer Answer(string label, double level)
    {
        return new JudgeAnswer("choice", label, level, 1.0,
            new Dictionary<string, double> { [label] = 1.0 });
    }
}
