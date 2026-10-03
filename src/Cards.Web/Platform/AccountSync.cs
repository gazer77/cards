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
    public string? Code => _record?.Code;

    /// <summary>Whether this server keeps accounts; false until it has answered.</summary>
    public bool Available { get; private set; }

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
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<AccountData>());
    }

    private async Task Remember()
        => await (await Module()).InvokeVoidAsync("setRecord", JsonSerializer.Serialize(_record));
}
