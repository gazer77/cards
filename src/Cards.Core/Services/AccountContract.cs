namespace Cards.Services;

/// <summary>
/// The account API, as client and server both see it. The code travels in a header, not
/// the address, so it is not written into a server's request log.
/// </summary>
public static class AccountContract
{
    public const string Path       = "/api/account";
    public const string CodePath   = "/api/account/code";
    public const string SetupPath  = "/api/account/admin";
    public const string AdminPath  = "/api/admin/accounts";
    public const string CodeHeader = "X-Account-Code";
}

/// <summary>
/// What an account may do on its server. Every account is a player; a manager looks
/// after the tables and the games on offer; an admin looks after the people, roles
/// included.
/// </summary>
public static class AccountRoles
{
    public const string Player  = "player";
    public const string Manager = "manager";
    public const string Admin   = "admin";

    public static readonly IReadOnlyList<string> All = [Player, Manager, Admin];

    /// <summary>A known role, or player.</summary>
    public static string Of(string? role) => role is Manager or Admin ? role : Player;

    /// <summary>Whether <paramref name="role"/> may do what <paramref name="needed"/> may: an admin, all a manager can.</summary>
    public static bool AtLeast(string? role, string needed)
        => Rank(Of(role)) >= Rank(needed);

    private static int Rank(string role) => role switch { Admin => 2, Manager => 1, _ => 0 };
}

/// <summary>An account as the admin page lists it: who it is to people, never its code.</summary>
public sealed class AccountSummary
{
    public string Id { get; set; } = "";
    /// <summary>The name its settings carry — what the person calls themselves at a table.</summary>
    public string? Name { get; set; }
    public string Role { get; set; } = AccountRoles.Player;
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public int SavedGames { get; set; }
}

/// <summary>An open table, as a manager sees it.</summary>
public sealed class RoomSummary
{
    public string Code { get; set; } = "";
    public string GameName { get; set; } = "";
    public bool Started { get; set; }
    public DateTime LastActivity { get; set; }
    public List<SeatSummary> Seats { get; set; } = [];
}

public sealed class SeatSummary
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool IsComputer { get; set; }
    public bool IsConnected { get; set; }
    /// <summary>The account sitting here, when known — what a ban is applied to.</summary>
    public string? AccountId { get; set; }
}

public static class ManagerContract
{
    public const string RoomsPath   = "/api/manage/rooms";
    public const string BansPath    = "/api/manage/bans";
    /// <summary>Which games the server offers: anyone may read it, a manager changes it.</summary>
    public const string CatalogPath = "/api/catalog";
}

public sealed class CatalogState
{
    /// <summary>Game ids turned off on this server; every other game is offered.</summary>
    public List<string> Off { get; set; } = [];
}

public sealed class OfferChange
{
    public bool Offered { get; set; }
}

public sealed class RoleChange
{
    public string Role { get; set; } = AccountRoles.Player;
}

public sealed class AdminSetup
{
    public string Setup { get; set; } = "";
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

    /// <summary>What this account may do here. Sent down, never taken from a client.</summary>
    public string Role { get; set; } = AccountRoles.Player;

    /// <summary>This server has no admin yet: the setup code in its log makes one.</summary>
    public bool AdminNeeded { get; set; }
}
