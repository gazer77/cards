using Cards.Engine.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cards.Server.Tests;

/// <summary>
/// The server run as an API behind a front end hosted somewhere else: no web app of its
/// own, and the hub open to the front end's origin — and only to it.
/// </summary>
public sealed class ApiModeTests : IClassFixture<ApiModeTests.ApiServer>
{
    public sealed class ApiServer : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Tables:ServeWebClient", "false");
            builder.UseSetting("Tables:AllowedOrigins:0", "https://cards.example");
        }
    }

    private readonly ApiServer _server;
    public ApiModeTests(ApiServer server) => _server = server;

    [Fact]
    public async Task It_serves_no_web_app()
    {
        var client = _server.CreateClient();
        var root = await client.GetStringAsync("/");
        Assert.DoesNotContain("<html", root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TableHubContract.Path, root);

        var page = await client.GetAsync("/room/ABCDE");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, page.StatusCode);
    }

    [Theory]
    [InlineData("https://cards.example", true)]
    [InlineData("https://elsewhere.example", false)]
    public async Task The_hub_is_open_to_the_front_ends_origin_only(string origin, bool allowed)
    {
        var client = _server.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, TableHubContract.Path + "/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);
        bool granted = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
                    && values.Contains(origin);
        Assert.Equal(allowed, granted);
    }
}
