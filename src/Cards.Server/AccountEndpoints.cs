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
                ? Results.Ok(new AccountData { Version = account.Version, Storage = account.Storage })
                : Results.NotFound();
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

    /// <summary>This server keeps no accounts — no folder for them. 501, so a client can tell it from a wrong code.</summary>
    private static IResult Off() => Results.StatusCode(StatusCodes.Status501NotImplemented);
}
