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
builder.Services.AddHostedService<RoomHousekeeping>();

var app = builder.Build();

if (allowedOrigins.Length > 0) app.UseCors();

if (serveWebClient)
{
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.MapHub<TableHub>(TableHubContract.Path);
app.MapGet("/health", () => "ok");

if (serveWebClient) app.MapFallbackToFile("index.html");
else                app.MapGet("/", () => "Cards table server. The hub is at " + TableHubContract.Path + ".");

app.Run();

/// <summary>Visible to the tests, which run the server in memory.</summary>
public partial class Program;
