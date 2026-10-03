using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Hosting;
using Cards.Server;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// Two ways to run, chosen in configuration ("Tables" in appsettings.json, or on the
// command line as --Tables:ServeWebClient=false and so on):
//
//   • Together (the default): this serves the web app as well as the hub, so a browser
//     reaches both at one address and there is one thing to run.
//   • As an API: ServeWebClient false, and the web app is hosted elsewhere — a static
//     host, a CDN, the Blazor dev server. AllowedOrigins lists the addresses that
//     front end is served from, since a browser calls the hub across origins then.
var serveWebClient = builder.Configuration.GetValue("Tables:ServeWebClient", true);
var allowedOrigins = builder.Configuration.GetSection("Tables:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddSignalR(o =>
{
    // A view carries a whole table; Hand and Foot at six seats is the largest.
    o.MaximumReceiveMessageSize = 256 * 1024;
    o.AddFilter<RefusalFilter>();
});

if (allowedOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));   // SignalR's negotiation carries credentials

// The same embedded definitions the clients carry, so server and browser can never
// disagree about what a game is.
builder.Services.AddSingleton<IGameAssetSource, EmbeddedGameAssetSource>();
builder.Services.AddSingleton<GameLoader>();

// The rooms themselves live in Cards.Hosting, which a phone hosting a game runs too;
// this project only carries them over SignalR.
builder.Services.AddSingleton<ITableClients, SignalRTableClients>();
builder.Services.AddSingleton<RoomService>();

// Rooms kept on disk, so a restart — every deploy is one — does not end every game.
// Tables:RoomDirectory names the folder; under systemd, StateDirectory= gives one
// ($STATE_DIRECTORY) that is used when nothing is configured. Neither: rooms live only
// as long as the process.
var roomDirectory = builder.Configuration["Tables:RoomDirectory"] is { Length: > 0 } configured
    ? Path.GetFullPath(configured, builder.Environment.ContentRootPath)
    : Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } state
        ? Path.Combine(state.Split(':')[0], "rooms")
        : null;
if (roomDirectory is not null)
    builder.Services.AddSingleton<IRoomStore>(new FileRoomStore(roomDirectory));

// Accounts without details (a six-word code each), kept beside the rooms. In development
// with nothing configured, under the user's local app data; with no folder at all, the
// account endpoints answer that accounts are off and the app plays on without them.
var accountDirectory = builder.Configuration["Accounts:Directory"] is { Length: > 0 } accountsConfigured
    ? Path.GetFullPath(accountsConfigured, builder.Environment.ContentRootPath)
    : Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } stateDir
        ? Path.Combine(stateDir.Split(':')[0], "accounts")
        : builder.Environment.IsDevelopment()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cards", "dev-accounts")
            : null;
if (accountDirectory is not null)
    builder.Services.AddSingleton(new AccountStore(accountDirectory));

// Codes cannot be guessed, but nobody gets to try quickly either.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("account", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("account-create", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromHours(1) }));
});
builder.Services.AddHostedService<RoomHousekeeping>();

var app = builder.Build();

if (allowedOrigins.Length > 0) app.UseCors();

if (serveWebClient)
{
    // Breakpoints in the web app's own code, when Visual Studio launches the server.
    if (app.Environment.IsDevelopment()) app.UseWebAssemblyDebugging();

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.MapHub<TableHub>(TableHubContract.Path);
app.MapGet("/health", () => "ok");

app.UseRateLimiter();
AccountEndpoints.Map(app);

if (serveWebClient) app.MapFallbackToFile("index.html");
else                app.MapGet("/", () => "Cards table server. The hub is at " + TableHubContract.Path + ".");

// Before the first connection: a device rejoining a room not yet restored would be told
// its table had closed, and forget it.
await app.Services.GetRequiredService<RoomService>().RestoreAsync();
app.Logger.LogInformation(roomDirectory is null
    ? "Rooms are not kept across restarts (no Tables:RoomDirectory)"
    : $"Rooms are kept in {roomDirectory}");

app.Run();

/// <summary>Visible to the tests, which run the server in memory.</summary>
public partial class Program;
