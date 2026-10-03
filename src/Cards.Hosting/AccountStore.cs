using System.Collections.Concurrent;
using System.Text.Json;
using Cards.Services;

namespace Cards.Hosting;

/// <summary>
/// Accounts without details: each is a code (<see cref="AccountCode"/>) and what the
/// player's devices keep — their settings and saved games — so a code typed on a new
/// phone brings it all across. One file per account; the server keeps the code's hash,
/// never the code, and does not read what it holds for them.
/// </summary>
public sealed class AccountStore
{
    /// <summary>What an account may hold, at most: a few saved games and settings, with room to spare.</summary>
    public const int MaxEntries = 200;
    public const int MaxBytes   = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, StoredAccount> _byHash = new();
    private readonly object _write = new();

    public AccountStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(path), Json) is { } account)
                    _byHash[account.CodeHash] = account;
            }
            catch (Exception) { /* a damaged file is one account lost, not every account */ }
        }
    }

    public int Count => _byHash.Count;

    public IEnumerable<StoredAccount> All => _byHash.Values;

    public StoredAccount? ById(string id) => _byHash.Values.FirstOrDefault(a => a.Id == id);

    // ── Roles ─────────────────────────────────────────────────────────────────

    private string? _setupCode;

    /// <summary>Whether no account here is an admin — so nobody can give out roles yet.</summary>
    public bool AdminNeeded => !_byHash.Values.Any(a => a.Role == AccountRoles.Admin);

    /// <summary>
    /// While there is no admin, a one-time code that makes one: the server writes it to its
    /// log at start, so only someone who can read the server's log can claim the role.
    /// Kept in memory only, new each start, and gone once used.
    /// </summary>
    public string? SetupCode => AdminNeeded ? _setupCode ??= string.Join(' ', AccountCode.New().Split(' ').Take(4)) : null;

    /// <summary>Makes the account an admin, given the setup code. False for a wrong code, or once there is an admin.</summary>
    public bool ClaimAdmin(StoredAccount account, string? setup)
    {
        if (!AdminNeeded || _setupCode is null) return false;
        var typed = string.Join(' ', (setup ?? "").ToLowerInvariant()
            .Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries));
        if (typed != _setupCode) return false;

        SetRole(account, AccountRoles.Admin);
        _setupCode = null;
        return true;
    }

    /// <summary>
    /// Gives an account a role. Refused when it would leave the server without an admin —
    /// the last admin cannot step down, or nobody could give roles out again.
    /// </summary>
    public bool SetRole(StoredAccount account, string role)
    {
        role = AccountRoles.Of(role);
        if (account.Role == AccountRoles.Admin && role != AccountRoles.Admin
            && _byHash.Values.Count(a => a.Role == AccountRoles.Admin) == 1)
            return false;

        lock (_write)
        {
            account.Role = role;
            Write(account);
        }
        return true;
    }

    /// <summary>Removes an account for good, unless it is the last admin.</summary>
    public bool Delete(StoredAccount account)
    {
        if (account.Role == AccountRoles.Admin && _byHash.Values.Count(a => a.Role == AccountRoles.Admin) == 1)
            return false;
        lock (_write)
        {
            _byHash.TryRemove(account.CodeHash, out _);
            File.Delete(Path.Combine(_directory, account.Id + ".json"));
        }
        return true;
    }

    /// <summary>The name the account's settings carry, for people to recognise it by — never its code.</summary>
    public static string? NameOf(StoredAccount account)
    {
        if (!account.Storage.TryGetValue("cards.settings", out var raw)) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return settings?.GetValueOrDefault("player_name") is { Length: > 0 } name && name != "Player" ? name : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A new account, and its code — the only time the code exists on the server.</summary>
    public (string Code, StoredAccount Account) Create()
    {
        string code = AccountCode.New();
        var account = new StoredAccount { Id = Guid.NewGuid().ToString("N"), CodeHash = AccountCode.Hash(code) };
        lock (_write) Write(account);
        _byHash[account.CodeHash] = account;
        return (code, account);
    }

    /// <summary>The account a code opens, or null — for a code that is not one, or not anyone's.</summary>
    public StoredAccount? Find(string? typed)
        => AccountCode.Normalize(typed) is { } code && _byHash.TryGetValue(AccountCode.Hash(code), out var account)
            ? account : null;

    /// <summary>
    /// Replaces what the account holds, and returns the new version. Refused (null) when
    /// it is more than an account may hold.
    /// </summary>
    public long? Save(StoredAccount account, Dictionary<string, string> storage)
    {
        if (storage.Count > MaxEntries) return null;
        if (storage.Sum(kv => (long)kv.Key.Length + kv.Value.Length) * 2 > MaxBytes) return null;

        lock (_write)
        {
            account.Storage = new(storage);
            account.Version++;
            account.Updated = DateTime.UtcNow;
            Write(account);
            return account.Version;
        }
    }

    /// <summary>A new code for the account; the old one stops working at once.</summary>
    public string NewCode(StoredAccount account)
    {
        string code = AccountCode.New();
        lock (_write)
        {
            _byHash.TryRemove(account.CodeHash, out _);
            account.CodeHash = AccountCode.Hash(code);
            Write(account);
            _byHash[account.CodeHash] = account;
        }
        return code;
    }

    private void Write(StoredAccount account)
    {
        string path = Path.Combine(_directory, account.Id + ".json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(account, Json));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class StoredAccount
{
    public string Id { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    /// <summary>What the account may do here — <see cref="AccountRoles"/>.</summary>
    public string Role { get; set; } = AccountRoles.Player;

    /// <summary>Bumped on every save, so a device can tell it is behind.</summary>
    public long Version { get; set; }

    /// <summary>What the player's devices keep, as they keep it: storage key → value.</summary>
    public Dictionary<string, string> Storage { get; set; } = [];
}
