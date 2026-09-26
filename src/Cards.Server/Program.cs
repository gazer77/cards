using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(o =>
{
    // A view carries a whole table; Hand and Foot at six seats is the largest.
    o.MaximumReceiveMessageSize = 256 * 1024;
});

// The same embedded definitions the clients carry, so server and browser can never
// disagree about what a game is.
builder.Services.AddSingleton<IGameAssetSource, EmbeddedGameAssetSource>();
builder.Services.AddSingleton<GameLoader>();
builder.Services.AddSingleton<RoomService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoomService>());

var app = builder.Build();

// The web client, served from here so the table and the hub share one address.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapHub<TableHub>(TableHubContract.Path);
app.MapGet("/health", () => "ok");
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Visible to the tests, which run the server in memory.</summary>
public partial class Program;
