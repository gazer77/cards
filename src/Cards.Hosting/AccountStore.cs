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
                {
                    _byHash[account.CodeHash] = account;
                    foreach (var device in account.DeviceKeyHashes) _byHash[device] = account;
                    if (account.Username is { } name) _byUsername[name] = account;
                }
            }
            catch (Exception) { /* a damaged file is one account lost, not every account */ }
        }
    }

    public int Count => All.Count();

    /// <summary>Every account once — the key index holds an account under its code and each signed-in device.</summary>
    public IEnumerable<StoredAccount> All => _byHash.Values.Distinct();

    public StoredAccount? ById(string id) => All.FirstOrDefault(a => a.Id == id);

    // ── Roles ─────────────────────────────────────────────────────────────────

    private string? _setupCode;

    /// <summary>Whether no account here is an admin — so nobody can give out roles yet.</summary>
    public bool AdminNeeded => !All.Any(a => a.Role == AccountRoles.Admin);

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
            && All.Count(a => a.Role == AccountRoles.Admin) == 1)
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
        if (account.Role == AccountRoles.Admin && All.Count(a => a.Role == AccountRoles.Admin) == 1)
            return false;
        lock (_write)
        {
            _byHash.TryRemove(account.CodeHash, out _);
            foreach (var device in account.DeviceKeyHashes) _byHash.TryRemove(device, out _);
            if (account.Username is { } name) _byUsername.TryRemove(name, out _);
            File.Delete(Path.Combine(_directory, account.Id + ".json"));
        }
        return true;
    }

    /// <summary>Bars an account from the tables on this server, or lets it back.</summary>
    public void SetBanned(StoredAccount account, bool banned)
    {
        lock (_write)
        {
            account.BannedAt = banned ? DateTime.UtcNow : null;
            Write(account);
        }
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

    /// <summary>
    /// The account a code — or a device key from a password sign-in — opens, or null: for a
    /// code that is not one, or not anyone's.
    /// </summary>
    public StoredAccount? Find(string? typed)
    {
        if (typed is not null && typed.StartsWith(DeviceKeyPrefix, StringComparison.Ordinal))
            return _byHash.TryGetValue(DeviceKeyHash(typed), out var signedIn) ? signedIn : null;
        return AccountCode.Normalize(typed) is { } code && _byHash.TryGetValue(AccountCode.Hash(code), out var account)
            ? account : null;
    }

    // ── Usernames and passwords ───────────────────────────────────────────────

    public const string DeviceKeyPrefix = "dk_";
    public const int MaxDevices = 20;
    public const int SignInTries = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, StoredAccount> _byUsername = new(StringComparer.OrdinalIgnoreCase);

    private static string DeviceKeyHash(string key)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("cards-device:" + key)));

    /// <summary>Why a username will not do, or null: three to twenty-four letters, digits, dots, dashes or underscores, starting with a letter or digit.</summary>
    public static string? CheckUsername(string? username)
        => username is { Length: >= 3 and <= 24 } && System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9][A-Za-z0-9._-]*$")
            ? null : "A username is 3 to 24 letters or digits, and may use . _ or -.";

    public static string? CheckPassword(string? password)
        => password is { Length: >= 6 and <= 128 } ? null : "A password is at least 6 characters.";

    /// <summary>
    /// Gives the account a username and password, or changes them. The reason when it
    /// cannot: a name or password that will not do, or a name someone else has.
    /// </summary>
    public string? SetLogin(StoredAccount account, string username, string password)
    {
        if ((CheckUsername(username) ?? CheckPassword(password)) is { } why) return why;
        lock (_write)
        {
            if (_byUsername.TryGetValue(username, out var holder) && holder.Id != account.Id)
                return "That username is taken.";
            if (account.Username is { } old) _byUsername.TryRemove(old, out _);

            account.Username      = username;
            account.PasswordHash  = Passwords.Hash(password);
            account.FailedSignIns = 0;
            account.LockedUntil   = null;
            _byUsername[username] = account;
            Write(account);
        }
        return null;
    }

    /// <summary>Takes the username and password off the account, and signs out every device that came in with them.</summary>
    public void RemoveLogin(StoredAccount account)
    {
        lock (_write)
        {
            if (account.Username is { } name) _byUsername.TryRemove(name, out _);
            foreach (var hash in account.DeviceKeyHashes) _byHash.TryRemove(hash, out _);
            account.Username = null;
            account.PasswordHash = null;
            account.DeviceKeyHashes.Clear();
            Write(account);
        }
    }

    /// <summary>
    /// Signs in with a username and password: a new key for this device, which opens the
    /// account as its code does. A wrong name and a wrong password answer the same, so the
    /// answer does not say which names exist; five wrong in a row lock the name a minute.
    /// </summary>
    public (StoredAccount? Account, string? DeviceKey, string? Why) SignIn(string? username, string? password)
    {
        const string Wrong = "That username and password do not match.";
        if (username is null || password is null || !_byUsername.TryGetValue(username, out var account))
            return (null, null, Wrong);

        lock (_write)
        {
            if (account.LockedUntil is { } until && until > DateTime.UtcNow)
                return (null, null, "Too many tries — wait a minute and try again.");

            if (!Passwords.Matches(password, account.PasswordHash))
            {
                if (++account.FailedSignIns >= SignInTries)
                {
                    account.LockedUntil   = DateTime.UtcNow + Lockout;
                    account.FailedSignIns = 0;
                }
                Write(account);
                return (null, null, Wrong);
            }

            string key = DeviceKeyPrefix + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                                                  .Replace('+', '-').Replace('/', '_');
            string hash = DeviceKeyHash(key);
            account.DeviceKeyHashes.Add(hash);
            while (account.DeviceKeyHashes.Count > MaxDevices)
            {
                _byHash.TryRemove(account.DeviceKeyHashes[0], out _);
                account.DeviceKeyHashes.RemoveAt(0);
            }
            account.FailedSignIns = 0;
            account.LockedUntil   = null;
            _byHash[hash] = account;
            Write(account);
            return (account, key, null);
        }
    }

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

    /// <summary>When a manager barred it from this server's tables; null while it may play.</summary>
    public DateTime? BannedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Banned => BannedAt is not null;

    /// <summary>Bumped on every save, so a device can tell it is behind.</summary>
    public long Version { get; set; }

    /// <summary>What the player's devices keep, as they keep it: storage key → value.</summary>
    public Dictionary<string, string> Storage { get; set; } = [];

    // ── An optional username and password ─────────────────────────────────────

    /// <summary>The name to sign in with, as typed when it was chosen; null for an account known by its code alone.</summary>
    public string? Username { get; set; }

    /// <summary>The password, salted and stretched (<see cref="Passwords"/>); never the password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Hashes of the keys handed to devices signed in with the password. The server
    /// cannot give such a device the account's code — it keeps only the code's hash — so
    /// each gets a key of its own instead, which opens the account as the code does.
    /// </summary>
    public List<string> DeviceKeyHashes { get; set; } = [];

    /// <summary>Wrong passwords in a row, and until when the name is locked after too many.</summary>
    public int FailedSignIns { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// <summary>
/// Passwords, kept as PBKDF2 (SHA-256, 100 000 rounds, a 16-byte salt each): slow to guess
/// even from a stolen file, and compared in constant time.
/// </summary>
public static class Passwords
{
    private const int Rounds = 100_000;

    public static string Hash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Rounds, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Rounds}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Matches(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int rounds)) return false;
        var salt     = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual   = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, rounds, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
