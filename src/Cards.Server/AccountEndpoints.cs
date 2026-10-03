using Cards.Hosting;
using Cards.Services;

namespace Cards.Server;

/// <summary>
/// The account API: make an account, read and replace what it holds, and change its code.
/// The code comes in a header (<see cref="AccountContract.CodeHeader"/>); an unknown code
/// is answered exactly like a wrong one, so the answers say nothing about which codes exist.
/// </summary>
public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        var store = app.Services.GetService<AccountStore>();

        app.MapPost(AccountContract.Path, () =>
        {
            if (store is null) return Off();
            var (code, account) = store.Create();
            return Results.Ok(new AccountCreated { Code = code, Version = account.Version });
        }).RequireRateLimiting("account-create");

        app.MapGet(AccountContract.Path, (HttpRequest request) =>
        {
            if (store is null) return Off();
            return Open(store, request) is { } account
                ? Results.Ok(new AccountData
                {
                    Version = account.Version, Storage = account.Storage,
                    Role = account.Role, AdminNeeded = store.AdminNeeded, Username = account.Username,
                })
                : Results.NotFound();
        }).RequireRateLimiting("account");

        // ── A username and password, for those who want one ───────────────────

        var options = app.Services.GetRequiredService<ServerOptions>();

        app.MapGet(AccountContract.OptionsPath, () => Results.Ok(options.Current));

        app.MapPut(AccountContract.OptionsPath, (HttpRequest request, ServerOptionsView change) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            options.Set(change);
            return Results.Ok(options.Current);
        }).RequireRateLimiting("account");

        app.MapPut(AccountContract.LoginPath, (HttpRequest request, LoginDetails login) =>
        {
            if (store is null) return Off();
            if (!options.Current.UsernamesOffered) return NotOffered();
            if (Open(store, request) is not { } account) return Results.NotFound();
            return store.SetLogin(account, login.Username.Trim(), login.Password) is { } why
                ? (why.Contains("taken") ? Results.Conflict(why) : Results.BadRequest(why))
                : Results.Ok(new SignedIn { Username = account.Username! });
        }).RequireRateLimiting("account");

        app.MapDelete(AccountContract.LoginPath, (HttpRequest request) =>
        {
            if (store is null) return Off();
            if (Open(store, request) is not { } account) return Results.NotFound();
            store.RemoveLogin(account);
            return Results.NoContent();
        }).RequireRateLimiting("account");

        app.MapPost(AccountContract.SignInPath, (LoginDetails login) =>
        {
            if (store is null) return Off();
            if (!options.Current.UsernamesOffered) return NotOffered();
            var (account, key, why) = store.SignIn(login.Username.Trim(), login.Password);
            return account is null
                ? Results.Json(why, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new SignedIn { Key = key!, Username = account.Username! });
        }).RequireRateLimiting("account");

        app.MapDelete(AccountContract.AdminPath + "/{id}/login", (HttpRequest request, string id) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            if (store.ById(id) is not { } account) return Results.NotFound();
            store.RemoveLogin(account);
            return Results.NoContent();
        }).RequireRateLimiting("account");

        // The first admin: whoever can read the server's log has the setup code.
        app.MapPost(AccountContract.SetupPath, (HttpRequest request, AdminSetup setup) =>
        {
            if (store is null) return Off();
            if (Open(store, request) is not { } account) return Results.NotFound();
            return store.ClaimAdmin(account, setup.Setup)
                ? Results.Ok(new RoleChange { Role = account.Role })
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        }).RequireRateLimiting("account");

        // ── Admin: the people ─────────────────────────────────────────────────

        app.MapGet(AccountContract.AdminPath, (HttpRequest request) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            return Results.Ok(store.All.OrderBy(a => a.Created).Select(a => new AccountSummary
            {
                Id = a.Id, Name = AccountStore.NameOf(a), Role = a.Role, Username = a.Username, Created = a.Created, Updated = a.Updated,
                SavedGames = a.Storage.Keys.Count(k => k.StartsWith("cards.save.") && k != "cards.save.save_index"),
            }).ToList());
        }).RequireRateLimiting("account");

        app.MapPut(AccountContract.AdminPath + "/{id}/role", (HttpRequest request, string id, RoleChange change) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            if (store.ById(id) is not { } account) return Results.NotFound();
            return store.SetRole(account, change.Role)
                ? Results.Ok(new RoleChange { Role = account.Role })
                : Results.Conflict("The last admin cannot step down.");
        }).RequireRateLimiting("account");

        // A person who lost their code: the admin gives them a new one to type in.
        app.MapPost(AccountContract.AdminPath + "/{id}/code", (HttpRequest request, string id) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            return store.ById(id) is { } account
                ? Results.Ok(new AccountCreated { Code = store.NewCode(account), Version = account.Version })
                : Results.NotFound();
        }).RequireRateLimiting("account");

        app.MapDelete(AccountContract.AdminPath + "/{id}", (HttpRequest request, string id) =>
        {
            if (store is null) return Off();
            if (Require(store, request, AccountRoles.Admin) is { } refused) return refused;
            if (store.ById(id) is not { } account) return Results.NotFound();
            return store.Delete(account) ? Results.NoContent() : Results.Conflict("The last admin cannot be deleted.");
        }).RequireRateLimiting("account");

        app.MapPut(AccountContract.Path, (HttpRequest request, AccountData data) =>
        {
            if (store is null) return Off();
            if (Open(store, request) is not { } account) return Results.NotFound();
            return store.Save(account, data.Storage) is { } version
                ? Results.Ok(new AccountData { Version = version })
                : Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }).RequireRateLimiting("account");

        app.MapPost(AccountContract.CodePath, (HttpRequest request) =>
        {
            if (store is null) return Off();
            return Open(store, request) is { } account
                ? Results.Ok(new AccountCreated { Code = store.NewCode(account), Version = account.Version })
                : Results.NotFound();
        }).RequireRateLimiting("account");
    }

    private static StoredAccount? Open(AccountStore store, HttpRequest request)
        => store.Find(request.Headers[AccountContract.CodeHeader].FirstOrDefault());

    /// <summary>
    /// Refuses a caller without the role: 404 for no account at all, 403 for one without
    /// the standing. Null when they may go on. The role is the server's record, never the
    /// client's word.
    /// </summary>
    public static IResult? Require(AccountStore store, HttpRequest request, string role)
        => Open(store, request) is not { } caller ? Results.NotFound()
         : !AccountRoles.AtLeast(caller.Role, role) ? Results.StatusCode(StatusCodes.Status403Forbidden)
         : null;

    /// <summary>The admin has turned usernames and passwords off here.</summary>
    private static IResult NotOffered()
        => Results.Json("Usernames and passwords are not offered on this server.", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>This server keeps no accounts — no folder for them. 501, so a client can tell it from a wrong code.</summary>
    private static IResult Off() => Results.StatusCode(StatusCodes.Status501NotImplemented);
}
