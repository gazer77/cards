using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Cards.App;
using Cards.Engine;
using Cards.Services;
using Cards.Web;
using Cards.Web.Platform;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Game content is embedded in Cards.Core, so the browser reads the same definitions
// the phone and the server do — no fetch, no version skew.
builder.Services.AddSingleton<IGameAssetSource, EmbeddedGameAssetSource>();
builder.Services.AddSingleton<GameLoader>();

builder.Services.AddSingleton<BrowserSettingsStore>();
builder.Services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<BrowserSettingsStore>());
builder.Services.AddSingleton<BrowserSaveStore>();
builder.Services.AddSingleton<ISaveStore>(sp => sp.GetRequiredService<BrowserSaveStore>());

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<GameSaveService>();
builder.Services.AddSingleton<HelpService>();
builder.Services.AddSingleton<Cards.Web.Platform.BrowserTableSounds>();
builder.Services.AddTransient<GameTableViewModel>();

// Shared tables. The table server also serves this app, so its hub is at the address the
// page came from; "TableServer" in wwwroot/appsettings.json points elsewhere when the
// app is run on its own (the dev server) against a server running separately.
var tableServer = builder.Configuration["TableServer"] is { Length: > 0 } configured
    ? new Uri(configured)
    : new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddSingleton(sp =>
    new TableConnection(new Uri(tableServer, Cards.Engine.Shared.TableHubContract.Path.TrimStart('/')),
                        sp.GetRequiredService<ISettingsStore>())
    {
        // A table knows which account sits at it, so a manager's ban reaches the person.
        AccountCode = () => sp.GetRequiredService<AccountSync>().Code,
    });

// The player's account lives on the same server as the tables.
builder.Services.AddSingleton(sp => new AccountSync(
    sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
    new HttpClient { BaseAddress = tableServer, Timeout = TimeSpan.FromSeconds(6) }));

var host = builder.Build();

// The account first: what another device saved comes down into this browser's storage
// before the settings and saves below read it.
var account = host.Services.GetRequiredService<AccountSync>();
await account.StartAsync();

// localStorage is async but the settings and save surfaces are synchronous, so both
// stores are primed once here before anything reads them.
var settingsStore = host.Services.GetRequiredService<BrowserSettingsStore>();
await settingsStore.LoadAsync();

// The list of saved games is read once here so screens can show it without awaiting.
await host.Services.GetRequiredService<GameSaveService>().EnsureLoadedAsync();

// From here on, what changes goes up to the account.
settingsStore.Changed += account.NoteChange;
host.Services.GetRequiredService<BrowserSaveStore>().Changed += account.NoteChange;

await host.RunAsync();
