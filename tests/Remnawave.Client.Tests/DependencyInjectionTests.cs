using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Remnawave.Client.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task Client_is_bound_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remnawave:BaseUrl"] = "https://panel.example.com",
            ["Remnawave:ApiToken"] = "from-config",
            ["Remnawave:Timeout"] = "00:00:10",
            ["Remnawave:UseForwardedHeaders"] = "true",
            ["Remnawave:DefaultHeaders:X-Api-Key"] = "proxy-secret",
        }).Build();

        var stub = new StubHandler().Respond(HttpStatusCode.OK, Fixtures.UserJson);
        var services = new ServiceCollection();
        services.AddRemnawaveClient(configuration.GetSection(RemnawaveClientOptions.SectionName))
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var options = provider.GetRequiredService<IOptions<RemnawaveClientOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);

        var client = provider.GetRequiredService<RemnawaveApiClient>();
        var user = await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken).UnwrapAsync();

        Assert.Equal("alice", user.Username);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer from-config", request.Headers["Authorization"]);
        Assert.Equal("proxy-secret", request.Headers["X-Api-Key"]);
        Assert.Equal("https", request.Headers["X-Forwarded-Proto"]);
    }

    [Fact]
    public async Task Named_panels_are_resolved_as_keyed_services()
    {
        var eu = new StubHandler().Respond(HttpStatusCode.OK, Fixtures.UserJson);
        var us = new StubHandler().Respond(HttpStatusCode.OK, Fixtures.UserJson);
        var services = new ServiceCollection();
        services.AddRemnawaveClient("eu", o => { o.BaseUrl = new Uri("https://eu.example.com"); o.ApiToken = "eu"; })
            .ConfigurePrimaryHttpMessageHandler(() => eu);
        services.AddRemnawaveClient("us", o => { o.BaseUrl = new Uri("https://us.example.com"); o.ApiToken = "us"; })
            .ConfigurePrimaryHttpMessageHandler(() => us);

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<RemnawaveApiClient>("eu").Api.Users[1].GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IRemnawaveClientFactory>().CreateClient("us").Api.Users[1].GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("eu.example.com", Assert.Single(eu.Requests).Uri.Host);
        Assert.Equal("Bearer eu", eu.Requests[0].Headers["Authorization"]);
        Assert.Equal("us.example.com", Assert.Single(us.Requests).Uri.Host);
        Assert.Equal("Bearer us", us.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public void Missing_base_url_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddRemnawaveClient(o => o.ApiToken = "token");
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RemnawaveClientOptions>>().Value);
        Assert.Contains("BaseUrl is required", ex.Message);
    }
}
