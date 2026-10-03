namespace Cards.Services;

/// <summary>
/// The account API, as client and server both see it. The code travels in a header, not
/// the address, so it is not written into a server's request log.
/// </summary>
public static class AccountContract
{
    public const string Path       = "/api/account";
    public const string CodePath   = "/api/account/code";
    public const string CodeHeader = "X-Account-Code";
}

/// <summary>A new account: its code, shown to the player once and kept on the device.</summary>
public sealed class AccountCreated
{
    public string Code { get; set; } = "";
    public long Version { get; set; }
}

/// <summary>What an account holds, and which save of it this is.</summary>
public sealed class AccountData
{
    public long Version { get; set; }
    public Dictionary<string, string> Storage { get; set; } = [];
}
