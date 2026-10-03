using System.Net;
using System.Net.Http.Json;
using Cards.Hosting;
using Cards.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cards.Server.Tests;

/// <summary>
/// Accounts without details, over the wire: made with nothing asked, opened by their code
/// alone, carrying what a device keeps, and given a new code that retires the old.
/// </summary>
public sealed class AccountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cards-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _server;

    public AccountTests()
        => _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
               b.UseSetting("Accounts:Directory", _directory));

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static HttpRequestMessage With(HttpMethod method, string path, string code, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AccountContract.CodeHeader, code);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task An_account_is_made_with_nothing_asked_and_carries_what_a_device_keeps()
    {
        var http = _server.CreateClient();

        var made = await (await http.PostAsync(AccountContract.Path, null)).Content.ReadFromJsonAsync<AccountCreated>();
        Assert.NotNull(AccountCode.Normalize(made!.Code));

        var keeps = new Dictionary<string, string> { ["cards.settings"] = "{\"player_name\":\"Ana\"}", ["cards.save.index"] = "[]" };
        var saved = await http.SendAsync(With(HttpMethod.Put, AccountContract.Path, made.Code, new AccountData { Storage = keeps }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Another device, typing the code as a person would.
        string typed = made.Code.ToUpperInvariant().Replace(' ', '-');
        var held = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, typed))).Content.ReadFromJsonAsync<AccountData>();
        Assert.Equal(keeps, held!.Storage);
        Assert.Equal(1, held.Version);
    }

    [Fact]
    public async Task An_unknown_code_opens_nothing()
    {
        var http = _server.CreateClient();
        var response = await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, "amber otter maple river lamp cocoa"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var garbled = await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, "not a code at all"));
        Assert.Equal(HttpStatusCode.NotFound, garbled.StatusCode);   // the same answer, saying nothing more
    }

    [Fact]
    public async Task A_new_code_retires_the_old_one_and_keeps_the_account()
    {
        var http = _server.CreateClient();
        var made = await (await http.PostAsync(AccountContract.Path, null)).Content.ReadFromJsonAsync<AccountCreated>();
        await http.SendAsync(With(HttpMethod.Put, AccountContract.Path, made!.Code,
                                  new AccountData { Storage = new() { ["cards.settings"] = "{}" } }));

        var renewed = await (await http.SendAsync(With(HttpMethod.Post, AccountContract.CodePath, made.Code)))
                            .Content.ReadFromJsonAsync<AccountCreated>();
        Assert.NotEqual(made.Code, renewed!.Code);

        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, made.Code))).StatusCode);
        var held = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, renewed.Code))).Content.ReadFromJsonAsync<AccountData>();
        Assert.True(held!.Storage.ContainsKey("cards.settings"));
    }

    [Fact]
    public async Task More_than_an_account_may_hold_is_refused()
    {
        var http = _server.CreateClient();
        var made = await (await http.PostAsync(AccountContract.Path, null)).Content.ReadFromJsonAsync<AccountCreated>();

        var tooMany = Enumerable.Range(0, AccountStore.MaxEntries + 1).ToDictionary(i => $"cards.save.{i}", _ => "x");
        var response = await http.SendAsync(With(HttpMethod.Put, AccountContract.Path, made!.Code, new AccountData { Storage = tooMany }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public void The_server_keeps_no_code_and_its_accounts_outlive_it()
    {
        var store = new AccountStore(_directory);
        var (code, account) = store.Create();
        store.Save(account, new() { ["cards.settings"] = "{}" });

        string onDisk = string.Concat(Directory.GetFiles(_directory).Select(File.ReadAllText));
        foreach (var word in code.Split(' ')) Assert.DoesNotContain($"\"{word}", onDisk);
        Assert.DoesNotContain(code, onDisk);

        var again = new AccountStore(_directory);   // the server restarts
        Assert.Equal(account.Id, again.Find(code)!.Id);
    }
}
