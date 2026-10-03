using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cards.Services;
using Microsoft.JSInterop;

namespace Cards.Web.Platform;

/// <summary>
/// The player's account, kept in step with this browser. There is nothing to sign up
/// for: the first visit makes an account, and its code (<see cref="AccountCode"/>) is
/// shown in Settings to carry to another device. Settings and saved games go up a
/// moment after they change, and come down at start when another device has saved
/// something newer.
///
/// Everything here fails quietly. A server that keeps no accounts, or cannot be reached,
/// leaves the app exactly as it was without them.
/// </summary>
public sealed class AccountSync(IJSRuntime js, HttpClient http)
{
    private sealed class Record
    {
        public string Code { get; set; } = "";
        public long Version { get; set; }
        /// <summary>Changed here since it last went up.</summary>
        public bool Dirty { get; set; }
    }

    private IJSObjectReference? _module;
    private Record? _record;
    private CancellationTokenSource? _pending;

    /// <summary>The account's code, once there is one.</summary>
    public string? Code => _record?.Code is { } key && !IsDeviceKey(key) ? key : null;

    /// <summary>
    /// What this device opens the account with: its code, or — signed in with a password —
    /// a key of its own. Sent with every request; never shown.
    /// </summary>
    public string? Key => _record?.Code;

    private static bool IsDeviceKey(string key) => key.StartsWith("dk_", StringComparison.Ordinal);

    /// <summary>The username this account signs in with, if it has one.</summary>
    public string? Username { get; private set; }

    /// <summary>Whether this server lets players give their account a username and password.</summary>
    public bool UsernamesOffered { get; private set; }

    /// <summary>Whether this server keeps accounts; false until it has answered.</summary>
    public bool Available { get; private set; }

    /// <summary>What this account may do here, as the server last said.</summary>
    public string Role { get; private set; } = AccountRoles.Player;

    /// <summary>The server has no admin yet: Settings offers to claim it with the setup code.</summary>
    public bool AdminNeeded { get; private set; }

    public bool IsAdmin   => Role == AccountRoles.Admin;
    public bool IsManager => AccountRoles.AtLeast(Role, AccountRoles.Manager);

    public event Action? Changed;

    private async Task<IJSObjectReference> Module()
        => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/account.js");

    /// <summary>
    /// At start, before the settings and saves are read: make an account if there is
    /// none, and take in whatever another device saved since this one last looked.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            var module = await Module();
            var raw = await module.InvokeAsync<string?>("getRecord");
            _record = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<Record>(raw);

            try { UsernamesOffered = (await http.GetFromJsonAsync<ServerOptionsView>(AccountContract.OptionsPath))?.UsernamesOffered ?? false; }
            catch (Exception) { UsernamesOffered = false; }

            if (_record is null)
            {
                await CreateAsync();
                return;
            }

            var (status, held) = await Get(_record.Code);
            if (status == HttpStatusCode.NotFound)
            {
                // The server has no such account any more — its store was reset. What
                // this device has goes up again, under a new code.
                await CreateAsync();
                return;
            }
            if (held is null) return;

            if (_record.Dirty) await PushAsync();
            else if (held.Version > _record.Version)
            {
                await module.InvokeVoidAsync("write", held.Storage);
                _record.Version = held.Version;
                await Remember();
            }
        }
        catch (Exception) { /* no server, no accounts: the app plays on */ }
    }

    private async Task CreateAsync()
    {
        var created = await http.PostAsync(AccountContract.Path, null);
        if (!created.IsSuccessStatusCode) return;   // accounts are off here
        var made = await created.Content.ReadFromJsonAsync<AccountCreated>();
        _record = new Record { Code = made!.Code, Version = made.Version, Dirty = true };
        Available = true;
        await PushAsync();
        await Get(_record.Code);   // its role, and whether the server still wants an admin
    }

    // ── A username and password ───────────────────────────────────────────────

    /// <summary>Gives this account a username and password, or changes them. The reason, when the server will not.</summary>
    public async Task<string?> SetLoginAsync(string username, string password)
    {
        var response = await Send(HttpMethod.Put, AccountContract.LoginPath, new LoginDetails { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return await Reason(response);
        Username = (await response.Content.ReadFromJsonAsync<SignedIn>())!.Username;
        Changed?.Invoke();
        return null;
    }

    /// <summary>Takes the username and password off; devices that signed in with them are signed out.</summary>
    public async Task<bool> RemoveLoginAsync()
    {
        var response = await Send(HttpMethod.Delete, AccountContract.LoginPath);
        if (!response.IsSuccessStatusCode) return false;
        // This device too, if it came in by the password: it carries on under a new account.
        if (Key is { } key && IsDeviceKey(key)) { _record = null; await CreateAsync(); }
        Username = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Signs this device in to another account by its username and password: that
    /// account's settings and saved games replace this browser's. The reason when it cannot.
    /// </summary>
    public async Task<string?> SignInAsync(string username, string password)
    {
        var response = await http.PostAsJsonAsync(AccountContract.SignInPath, new LoginDetails { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return await Reason(response);

        var signedIn = (await response.Content.ReadFromJsonAsync<SignedIn>())!;
        var (_, held) = await Get(signedIn.Key);
        if (held is null) return "Signed in, but the account could not be read.";

        await (await Module()).InvokeVoidAsync("write", held.Storage);
        _record = new Record { Code = signedIn.Key, Version = held.Version };
        await Remember();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Turns usernames and passwords on or off for the whole server (the admin's choice).</summary>
    public async Task<bool> SetUsernamesOfferedAsync(bool offered)
    {
        var response = await Send(HttpMethod.Put, AccountContract.OptionsPath, new ServerOptionsView { UsernamesOffered = offered });
        if (response.IsSuccessStatusCode) UsernamesOffered = offered;
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ClearLoginForAsync(string id)
        => (await Send(HttpMethod.Delete, $"{AccountContract.AdminPath}/{id}/login")).IsSuccessStatusCode;

    private static async Task<string> Reason(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text is { Length: > 0 } ? text.Trim('"') : "That did not work.";
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    /// <summary>Claims the server's first admin with the setup code from its log.</summary>
    public async Task<bool> ClaimAdminAsync(string setup)
    {
        if (_record is null) return false;
        var response = await Send(HttpMethod.Post, AccountContract.SetupPath, new AdminSetup { Setup = setup });
        if (!response.IsSuccessStatusCode) return false;
        await Get(_record.Code);
        Changed?.Invoke();
        return true;
    }

    public async Task<List<AccountSummary>> AccountsAsync()
    {
        var response = await Send(HttpMethod.Get, AccountContract.AdminPath);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<List<AccountSummary>>())! : [];
    }

    /// <summary>Gives an account a role; the server's answer when it refuses (the last admin cannot step down).</summary>
    public async Task<string?> SetRoleAsync(string id, string role)
    {
        var response = await Send(HttpMethod.Put, $"{AccountContract.AdminPath}/{id}/role", new RoleChange { Role = role });
        if (response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync() is { Length: > 0 } why ? why.Trim('"') : "Not allowed.";
    }

    /// <summary>A new code for someone else's account, to give them; null when it cannot be done.</summary>
    public async Task<string?> NewCodeForAsync(string id)
    {
        var response = await Send(HttpMethod.Post, $"{AccountContract.AdminPath}/{id}/code");
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<AccountCreated>())!.Code : null;
    }

    public async Task<string?> DeleteAccountAsync(string id)
    {
        var response = await Send(HttpMethod.Delete, $"{AccountContract.AdminPath}/{id}");
        if (response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync() is { Length: > 0 } why ? why.Trim('"') : "Not allowed.";
    }

    // ── Managing the tables ───────────────────────────────────────────────────

    public async Task<List<RoomSummary>> RoomsAsync()
    {
        var response = await Send(HttpMethod.Get, ManagerContract.RoomsPath);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<List<RoomSummary>>())! : [];
    }

    public async Task<bool> CloseRoomAsync(string code)
        => (await Send(HttpMethod.Delete, $"{ManagerContract.RoomsPath}/{code}")).IsSuccessStatusCode;

    /// <summary>Takes someone off a table, and with <paramref name="ban"/> off every table here. The server's reason when it refuses.</summary>
    public async Task<string?> RemoveSeatAsync(string code, string seatId, bool ban)
    {
        var response = await Send(HttpMethod.Post, $"{ManagerContract.RoomsPath}/{code}/seats/{seatId}/remove?ban={(ban ? "true" : "false")}");
        if (response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync() is { Length: > 0 } why ? why.Trim('"') : "Not allowed.";
    }

    public async Task<List<AccountSummary>> BansAsync()
    {
        var response = await Send(HttpMethod.Get, ManagerContract.BansPath);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<List<AccountSummary>>())! : [];
    }

    /// <summary>Game ids this server has turned off; empty when it cannot say, so every game shows.</summary>
    public async Task<HashSet<string>> GamesOffAsync()
    {
        try
        {
            var response = await http.GetAsync(ManagerContract.CatalogPath);
            return response.IsSuccessStatusCode
                ? new HashSet<string>((await response.Content.ReadFromJsonAsync<CatalogState>())!.Off, StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception) { return []; }
    }

    public async Task<bool> SetOfferedAsync(string gameId, bool offered)
        => (await Send(HttpMethod.Put, $"{ManagerContract.CatalogPath}/{gameId}", new OfferChange { Offered = offered })).IsSuccessStatusCode;

    public async Task<bool> UnbanAsync(string id)
        => (await Send(HttpMethod.Delete, $"{ManagerContract.BansPath}/{id}")).IsSuccessStatusCode;

    /// <summary>A request as this account: its code in the header.</summary>
    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (_record is not null) request.Headers.Add(AccountContract.CodeHeader, _record.Code);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }

    /// <summary>Something the account carries changed here: send it up shortly, once.</summary>
    public void NoteChange()
    {
        if (_record is null) return;
        _record.Dirty = true;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ContinueWith(async t =>
        {
            if (!t.IsCanceled) await PushAsync();
        }, TaskScheduler.Default);
    }

    public async Task PushAsync()
    {
        if (_record is null) return;
        try
        {
            await Remember();   // dirty, in case the page closes before it lands
            var storage = await (await Module()).InvokeAsync<Dictionary<string, string>>("read");
            using var put = new HttpRequestMessage(HttpMethod.Put, AccountContract.Path)
            {
                Content = JsonContent.Create(new AccountData { Storage = storage }),
            };
            put.Headers.Add(AccountContract.CodeHeader, _record.Code);
            var response = await http.SendAsync(put);
            if (!response.IsSuccessStatusCode) return;

            var saved = await response.Content.ReadFromJsonAsync<AccountData>();
            _record.Version = saved!.Version;
            _record.Dirty   = false;
            await Remember();
        }
        catch (Exception) { /* tried again on the next change, and at the next start */ }
    }

    /// <summary>
    /// Takes this device over to another account by its code: its settings and saved
    /// games replace this browser's. False when the code is not an account's.
    /// </summary>
    public async Task<bool> UseCodeAsync(string typed)
    {
        if (AccountCode.Normalize(typed) is not { } code) return false;
        var (_, held) = await Get(code);
        if (held is null) return false;

        await (await Module()).InvokeVoidAsync("write", held.Storage);
        _record = new Record { Code = code, Version = held.Version };
        await Remember();
        Changed?.Invoke();
        return true;
    }

    /// <summary>A new code for this account; the old one stops working on every device.</summary>
    public async Task<bool> NewCodeAsync()
    {
        if (_record is null) return false;
        using var post = new HttpRequestMessage(HttpMethod.Post, AccountContract.CodePath);
        post.Headers.Add(AccountContract.CodeHeader, _record.Code);
        var response = await http.SendAsync(post);
        if (!response.IsSuccessStatusCode) return false;

        _record.Code = (await response.Content.ReadFromJsonAsync<AccountCreated>())!.Code;
        await Remember();
        Changed?.Invoke();
        return true;
    }

    private async Task<(HttpStatusCode Status, AccountData? Data)> Get(string code)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, AccountContract.Path);
        get.Headers.Add(AccountContract.CodeHeader, code);
        var response = await http.SendAsync(get);
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        Available = true;
        var data = await response.Content.ReadFromJsonAsync<AccountData>();
        if (data is not null && code == _record?.Code)
        {
            Role        = AccountRoles.Of(data.Role);
            AdminNeeded = data.AdminNeeded;
            Username    = data.Username;
        }
        return (response.StatusCode, data);
    }

    private async Task Remember()
        => await (await Module()).InvokeVoidAsync("setRecord", JsonSerializer.Serialize(_record));
}
