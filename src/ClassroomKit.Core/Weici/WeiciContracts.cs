namespace ClassroomKit.Core.Weici;

/// <summary>维词自动领读的结果；Code 与 weici-autoread 的 automation.js 保持同一套错误码。</summary>
public sealed record WeiciResult(bool Ok, string Code, string Message, string Raw = "")
{
    public static WeiciResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>一次自动领读的参数；账号密码始终由维词自己保存，这里不读取、不落盘。</summary>
public sealed record WeiciRunRequest(string FirstWord, string LastWord, string Pron, string Loop, bool DryRun);
