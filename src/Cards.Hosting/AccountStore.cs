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

    /// <summary>Bumped on every save, so a device can tell it is behind.</summary>
    public long Version { get; set; }

    /// <summary>What the player's devices keep, as they keep it: storage key → value.</summary>
    public Dictionary<string, string> Storage { get; set; } = [];
}
