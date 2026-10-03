using System.Net;
using Microsoft.Extensions.DependencyInjection;
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
    // Each test its own state folder: the accounts in it, and the game catalog beside them.
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cards-state-" + Guid.NewGuid().ToString("N"), "accounts");
    private readonly WebApplicationFactory<Program> _server;

    public AccountTests()
        => _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
               b.UseSetting("Accounts:Directory", _directory));

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true); } catch { }
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

    // ── Roles ─────────────────────────────────────────────────────────────────

    private async Task<string> NewAccount(HttpClient http)
        => (await (await http.PostAsync(AccountContract.Path, null)).Content.ReadFromJsonAsync<AccountCreated>())!.Code;

    /// <summary>The first admin, made with the setup code the server writes to its log.</summary>
    private async Task<string> Admin(HttpClient http)
    {
        string code = await NewAccount(http);
        var setup = _server.Services.GetRequiredService<AccountStore>().SetupCode!;
        var claimed = await http.SendAsync(With(HttpMethod.Post, AccountContract.SetupPath, code, new AdminSetup { Setup = setup }));
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        return code;
    }

    [Fact]
    public async Task The_first_admin_is_whoever_has_the_setup_code_and_there_is_only_one_first()
    {
        var http  = _server.CreateClient();
        var store = _server.Services.GetRequiredService<AccountStore>();
        string ana = await NewAccount(http);

        var before = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, ana))).Content.ReadFromJsonAsync<AccountData>();
        Assert.True(before!.AdminNeeded);
        Assert.Equal(AccountRoles.Player, before.Role);

        var wrong = await http.SendAsync(With(HttpMethod.Post, AccountContract.SetupPath, ana, new AdminSetup { Setup = "not the code" }));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        string setup = store.SetupCode!;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(With(HttpMethod.Post, AccountContract.SetupPath, ana,
                                                                    new AdminSetup { Setup = setup.ToUpperInvariant() }))).StatusCode);

        var after = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, ana))).Content.ReadFromJsonAsync<AccountData>();
        Assert.Equal(AccountRoles.Admin, after!.Role);
        Assert.False(after.AdminNeeded);
        Assert.Null(store.SetupCode);

        // The code is spent: nobody else becomes admin with it.
        string bo = await NewAccount(http);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(With(HttpMethod.Post, AccountContract.SetupPath, bo,
                                                                           new AdminSetup { Setup = setup }))).StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_sees_the_people_or_changes_them()
    {
        var http = _server.CreateClient();
        string player = await NewAccount(http);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(With(HttpMethod.Get, AccountContract.AdminPath, player))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(With(HttpMethod.Get, AccountContract.AdminPath, "amber otter maple river lamp cocoa"))).StatusCode);

        string admin = await Admin(http);
        var people = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.AdminPath, admin))).Content.ReadFromJsonAsync<List<AccountSummary>>();
        Assert.Equal(2, people!.Count);

        // A player made a manager is still refused the people.
        var playerId = _server.Services.GetRequiredService<AccountStore>().Find(player)!.Id;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(With(HttpMethod.Put, $"{AccountContract.AdminPath}/{playerId}/role", admin,
                                                                    new RoleChange { Role = AccountRoles.Manager }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(With(HttpMethod.Get, AccountContract.AdminPath, player))).StatusCode);
        var role = await (await http.SendAsync(With(HttpMethod.Get, AccountContract.Path, player))).Content.ReadFromJsonAsync<AccountData>();
        Assert.Equal(AccountRoles.Manager, role!.Role);
    }

    [Fact]
    public async Task An_admin_gives_a_lost_code_a_replacement_and_cannot_leave_the_server_without_an_admin()
    {
        var http  = _server.CreateClient();
        var store = _server.Services.GetRequiredService<AccountStore>();
        string admin  = await Admin(http);
        string player = await NewAccount(http);
        string playerId = store.Find(player)!.Id, adminId = store.Find(admin)!.Id;

        var issued = await (await http.SendAsync(With(HttpMethod.Post, $"{AccountContract.AdminPath}/{playerId}/code", admin)))
                           .Content.ReadFromJsonAsync<AccountCreated>();
        Assert.Null(store.Find(player));
        Assert.Equal(playerId, store.Find(issued!.Code)!.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(With(HttpMethod.Put, $"{AccountContract.AdminPath}/{adminId}/role", admin,
                                                                          new RoleChange { Role = AccountRoles.Player }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(With(HttpMethod.Delete, $"{AccountContract.AdminPath}/{adminId}", admin))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(With(HttpMethod.Delete, $"{AccountContract.AdminPath}/{playerId}", admin))).StatusCode);
        Assert.Null(store.Find(issued.Code));
    }

    // ── Managers at the tables ────────────────────────────────────────────────

    private async Task<string> Manager(HttpClient http, string admin)
    {
        string code = await NewAccount(http);
        var id = _server.Services.GetRequiredService<AccountStore>().Find(code)!.Id;
        await http.SendAsync(With(HttpMethod.Put, $"{AccountContract.AdminPath}/{id}/role", admin, new RoleChange { Role = AccountRoles.Manager }));
        return code;
    }

    [Fact]
    public async Task A_manager_sees_the_tables_takes_someone_off_and_bans_them_and_a_player_cannot()
    {
        var http  = _server.CreateClient();
        var rooms = _server.Services.GetRequiredService<RoomService>();
        var store = _server.Services.GetRequiredService<AccountStore>();
        string admin = await Admin(http), manager = await Manager(http, admin), troll = await NewAccount(http);

        var ana = await rooms.CreateAsync("c-ana", "hearts", 4, [], null, "Ana", accountCode: manager);
        var bo  = await rooms.JoinAsync("c-bo", ana.Code, "Bo", accountCode: troll);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(With(HttpMethod.Get, ManagerContract.RoomsPath, troll))).StatusCode);

        var tables = await (await http.SendAsync(With(HttpMethod.Get, ManagerContract.RoomsPath, manager))).Content.ReadFromJsonAsync<List<RoomSummary>>();
        var seat = tables!.Single(r => r.Code == ana.Code).Seats.Single(s => s.Name == "Bo");
        Assert.Equal(store.Find(troll)!.Id, seat.AccountId);

        var removed = await http.SendAsync(With(HttpMethod.Post, $"{ManagerContract.RoomsPath}/{ana.Code}/seats/{seat.Id}/remove?ban=true", manager));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // Off this table — the token is spent — and off every other.
        await Assert.ThrowsAsync<TableRefusal>(() => rooms.RejoinAsync("c-bo2", ana.Code, bo.Token));
        var refusal = await Assert.ThrowsAsync<TableRefusal>(() => rooms.CreateAsync("c-bo3", "war", 2, [], null, "Bo", accountCode: troll));
        Assert.Contains("can't play", refusal.Message);

        var bans = await (await http.SendAsync(With(HttpMethod.Get, ManagerContract.BansPath, manager))).Content.ReadFromJsonAsync<List<AccountSummary>>();
        Assert.Single(bans!);

        // Let back.
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(With(HttpMethod.Delete, $"{ManagerContract.BansPath}/{bans![0].Id}", manager))).StatusCode);
        await rooms.CreateAsync("c-bo4", "war", 2, [], null, "Bo", accountCode: troll);
    }

    [Fact]
    public async Task A_manager_cannot_ban_an_admin_or_another_manager()
    {
        var http  = _server.CreateClient();
        var rooms = _server.Services.GetRequiredService<RoomService>();
        string admin = await Admin(http), manager = await Manager(http, admin), other = await Manager(http, admin);

        var ana = await rooms.CreateAsync("m-ana", "hearts", 4, [], null, "Ana", accountCode: manager);
        await rooms.JoinAsync("m-ad", ana.Code, "Admin", accountCode: admin);
        await rooms.JoinAsync("m-ot", ana.Code, "Other", accountCode: other);

        foreach (var seat in new[] { "player1", "player2" })
        {
            var tried = await http.SendAsync(With(HttpMethod.Post, $"{ManagerContract.RoomsPath}/{ana.Code}/seats/{seat}/remove?ban=true", manager));
            Assert.Equal(HttpStatusCode.Conflict, tried.StatusCode);
        }
    }

    [Fact]
    public async Task A_manager_closes_a_table_and_it_is_gone()
    {
        var http  = _server.CreateClient();
        var rooms = _server.Services.GetRequiredService<RoomService>();
        string admin = await Admin(http);

        var ana = await rooms.CreateAsync("x-ana", "hearts", 4, [], null, "Ana");
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(With(HttpMethod.Delete, $"{ManagerContract.RoomsPath}/{ana.Code}", admin))).StatusCode);
        Assert.Null(rooms.Find(ana.Code));
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(With(HttpMethod.Delete, $"{ManagerContract.RoomsPath}/{ana.Code}", admin))).StatusCode);
    }

    [Fact]
    public async Task A_manager_turns_a_game_off_and_no_table_can_be_opened_for_it()
    {
        var http  = _server.CreateClient();
        var rooms = _server.Services.GetRequiredService<RoomService>();
        string admin = await Admin(http), manager = await Manager(http, admin), player = await NewAccount(http);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(With(HttpMethod.Put, $"{ManagerContract.CatalogPath}/war", player,
                                                                           new OfferChange { Offered = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(With(HttpMethod.Put, $"{ManagerContract.CatalogPath}/war", manager,
                                                                    new OfferChange { Offered = false }))).StatusCode);

        // Anyone may ask what is off — the home page does, before anyone is a manager.
        var catalog = await http.GetFromJsonAsync<CatalogState>(ManagerContract.CatalogPath);
        Assert.Equal(["war"], catalog!.Off);

        var refused = await Assert.ThrowsAsync<TableRefusal>(() => rooms.CreateAsync("w", "war", 2, [], null, "Ana"));
        Assert.Contains("not offered", refused.Message);

        // Kept: a restart offers the same games.
        Assert.Equal(["war"], new GameCatalog(Path.Combine(Path.GetDirectoryName(_directory)!, "catalog.json")).Off);

        await http.SendAsync(With(HttpMethod.Put, $"{ManagerContract.CatalogPath}/war", manager, new OfferChange { Offered = true }));
        await rooms.CreateAsync("w2", "war", 2, [], null, "Ana");
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
