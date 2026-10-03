using Cards.Hosting;
using Cards.Services;

namespace Cards.Server;

/// <summary>
/// What a manager does: looks after the tables — sees every open one, closes one, takes
/// someone off one — and the people barred from them. An admin can do all of it too.
/// Every request is checked against the caller's own role on the server.
/// </summary>
public static class ManagerEndpoints
{
    public static void Map(WebApplication app)
    {
        var store = app.Services.GetService<AccountStore>();
        var rooms = app.Services.GetRequiredService<RoomService>();

        app.MapGet(ManagerContract.RoomsPath, (HttpRequest request) =>
            Gate(store, request) ?? Results.Ok(rooms.Summaries())).RequireRateLimiting("account");

        app.MapDelete(ManagerContract.RoomsPath + "/{code}", async (HttpRequest request, string code) =>
            Gate(store, request) ?? (await rooms.CloseByManagerAsync(code) ? Results.NoContent() : Results.NotFound()))
            .RequireRateLimiting("account");

        // Off the table; with ?ban=true, off every table on this server too.
        app.MapPost(ManagerContract.RoomsPath + "/{code}/seats/{seatId}/remove", async (HttpRequest request, string code, string seatId, bool? ban) =>
        {
            if (Gate(store, request) is { } refused) return refused;
            var caller = store!.Find(request.Headers[AccountContract.CodeHeader].FirstOrDefault())!;

            var seat = rooms.Find(code)?.Seats.FirstOrDefault(s => s.Id == seatId);
            var target = seat?.AccountId is { } id ? store.ById(id) : null;
            if (ban == true && Protected(caller, target) is { } why) return Results.Conflict(why);

            var (removed, accountId) = await rooms.RemoveSeatAsync(code, seatId);
            if (!removed) return Results.NotFound();
            if (ban == true && accountId is not null && store.ById(accountId) is { } banned) store.SetBanned(banned, true);
            return Results.NoContent();
        }).RequireRateLimiting("account");

        app.MapGet(ManagerContract.BansPath, (HttpRequest request) =>
            Gate(store, request) ?? Results.Ok(store!.All.Where(a => a.Banned).OrderByDescending(a => a.BannedAt).Select(a => new AccountSummary
            {
                Id = a.Id, Name = AccountStore.NameOf(a), Role = a.Role, Created = a.Created, Updated = a.BannedAt ?? a.Updated,
            }).ToList())).RequireRateLimiting("account");

        app.MapDelete(ManagerContract.BansPath + "/{id}", (HttpRequest request, string id) =>
        {
            if (Gate(store, request) is { } refused) return refused;
            if (store!.ById(id) is not { } account) return Results.NotFound();
            store.SetBanned(account, false);
            return Results.NoContent();
        }).RequireRateLimiting("account");
    }

    /// <summary>A manager or better may pass; anyone else is refused. Accounts off means no managers either.</summary>
    private static IResult? Gate(AccountStore? store, HttpRequest request)
        => store is null ? Results.StatusCode(StatusCodes.Status501NotImplemented)
         : AccountEndpoints.Require(store, request, AccountRoles.Manager);

    /// <summary>
    /// Why this caller may not ban this account, if they may not: nobody bans themselves,
    /// a manager does not ban a manager or an admin, and nobody bans an admin.
    /// </summary>
    private static string? Protected(StoredAccount caller, StoredAccount? target)
        => target is null ? null
         : target.Id == caller.Id ? "You cannot ban yourself."
         : target.Role == AccountRoles.Admin ? "An admin cannot be banned."
         : target.Role == AccountRoles.Manager && caller.Role != AccountRoles.Admin ? "Only an admin can ban a manager."
         : null;
}
