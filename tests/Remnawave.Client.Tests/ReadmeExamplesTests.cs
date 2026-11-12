#pragma warning disable xUnit1051 // snippets mirror README.md verbatim
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Remnawave.Client.Models;
using Xunit;

namespace Remnawave.Client.Tests;

/// <summary>Keeps the README snippets compiling and working against a stubbed panel.</summary>
public sealed class ReadmeExamplesTests
{
    [Fact]
    public async Task Usage_section()
    {
        var stub = new StubHandler()
            .Respond(HttpStatusCode.Created, Fixtures.UserJson)                                  // create
            .Respond(HttpStatusCode.OK, Fixtures.UserJson)                                       // by id
            .Respond(HttpStatusCode.OK, Fixtures.UserJson)                                       // by username
            .Respond(HttpStatusCode.OK, """{"response":{"users":[],"total":0}}""")               // page
            .Respond(HttpStatusCode.OK, Fixtures.UserJson)                                       // patch
            .Respond(HttpStatusCode.OK, Fixtures.UserJson)                                       // disable
            .Respond(HttpStatusCode.OK, Fixtures.UserJson)                                       // reset traffic
            .Respond(HttpStatusCode.OK, """{"response":[]}""")                                   // nodes
            .Respond(HttpStatusCode.OK, """{"response":{"total":0,"internalSquads":[]}}""")      // squads
            .Respond(HttpStatusCode.OK, """{"response":{"uptime":1,"timestamp":1}}""");          // stats
        var remnawave = RemnawaveClientFactory.Create(new RemnawaveClientOptions { BaseUrl = new Uri("https://panel.example.com"), ApiToken = "t" }, stub);
        var squadUuid = Guid.NewGuid();

        User user = await remnawave.Api.Users.PostAsync(new CreateUserBodyDto
        {
            Username = "alice",
            ExpireAt = DateTimeOffset.UtcNow.AddDays(30),
            TrafficLimitBytes = 100L * 1024 * 1024 * 1024,
            TrafficLimitStrategy = UserTrafficLimitStrategy.Month,
            ActiveInternalSquads = [squadUuid],
        }).UnwrapAsync();

        User byId = await remnawave.Api.Users[user.Id!.Value].GetAsync().UnwrapAsync();
        User byUsername = await remnawave.Api.Users.ByUsername["alice"].GetAsync().UnwrapAsync();

        UsersResult page = await remnawave.Api.Users.GetAsync(r =>
        {
            r.QueryParameters.Start = 0;
            r.QueryParameters.Size = 100;
        }).UnwrapAsync();

        await remnawave.Api.Users.PatchAsync(new UpdateUserBodyDto { Id = user.Id, Description = "VIP" });
        await remnawave.Api.Users[user.Id!.Value].Actions.Disable.PostAsync();
        await remnawave.Api.Users[user.Id!.Value].Actions.ResetTraffic.PostAsync();

        List<Node> nodes = await remnawave.Api.Nodes.GetAsync().UnwrapAsync();
        InternalSquadsResult squads = await remnawave.Api.InternalSquads.GetAsync().UnwrapAsync();
        SystemStats stats = await remnawave.Api.System.Stats.GetAsync().UnwrapAsync();

        Assert.Equal(42L, byId.Id);
        Assert.Equal("alice", byUsername.Username);
        Assert.Equal(0L, page.Total);
        Assert.Empty(nodes);
        Assert.Equal(0L, squads.Total);
        Assert.NotNull(stats);
        Assert.Equal("https://panel.example.com/api/users?size=100&start=0", stub.Requests[3].Uri.ToString());
        Assert.Equal("https://panel.example.com/api/users/42/actions/disable", stub.Requests[5].Uri.ToString());
    }

    [Fact]
    public void Several_panels_section_compiles()
    {
        var services = new ServiceCollection();
        services.AddRemnawaveClient("eu", o => o.BaseUrl = new Uri("https://eu.example.com"));
        services.AddRemnawaveClient(o => o.BaseUrl = new Uri("https://panel.example.com"));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredKeyedService<RemnawaveApiClient>("eu"));
        Assert.NotNull(provider.GetRequiredService<RemnawaveApiClient>());
    }
}
