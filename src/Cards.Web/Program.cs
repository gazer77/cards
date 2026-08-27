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
builder.Services.AddTransient<GameTableViewModel>();

var host = builder.Build();

// localStorage is async but the settings and save surfaces are synchronous, so both
// stores are primed once here before anything reads them.
var settingsStore = host.Services.GetRequiredService<BrowserSettingsStore>();
await settingsStore.LoadAsync();

var loader    = host.Services.GetRequiredService<GameLoader>();
var saveStore = host.Services.GetRequiredService<BrowserSaveStore>();
await saveStore.LoadAsync((await loader.LoadAllAsync()).Select(g => g.Id));

await host.RunAsync();
