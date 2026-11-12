using System.Net;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Remnawave.Client.Models;
using Xunit;

namespace Remnawave.Client.Tests;

/// <summary>Edge cases found while verifying the generated client against the upstream document.</summary>
public sealed class VerificationTests
{
    private static (RemnawaveApiClient Client, StubHandler Stub) Create()
    {
        var stub = new StubHandler();
        return (RemnawaveClientFactory.Create(new RemnawaveClientOptions { BaseUrl = new Uri("https://panel.example.com"), ApiToken = "t" }, stub), stub);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Colliding_connection_paths_are_both_callable()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, """{"response":{"jobId":"job-1"}}""")
            .Respond(HttpStatusCode.OK, """{"response":{"isCompleted":false}}""");

        await client.Api.Connections.ByUser["42"].PostAsync(cancellationToken: Ct);   // POST /by-user/{userId}
        await client.Api.Connections.ByUser["job-1"].GetAsync(cancellationToken: Ct); // GET  /by-user/{jobId}

        Assert.Equal(HttpMethod.Post, stub.Requests[0].Method);
        Assert.Equal("https://panel.example.com/api/connections/by-user/42", stub.Requests[0].Uri.ToString());
        Assert.Equal(HttpMethod.Get, stub.Requests[1].Method);
        Assert.Equal("https://panel.example.com/api/connections/by-user/job-1", stub.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task Get_with_required_body_sends_the_body()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, """{"response":{}}""");

        var body = new GetSubpageConfigByShortUuidBodyDto { RequestHeaders = new() };
        body.RequestHeaders.AdditionalData["user-agent"] = "Happ/1.0";
        await client.Api.Subscriptions.SubpageConfig["abcd1234"].GetAsync(body, cancellationToken: Ct);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        using var json = JsonDocument.Parse(request.Body!);
        Assert.Equal("Happ/1.0", json.RootElement.GetProperty("requestHeaders").GetProperty("user-agent").GetString());
    }

    [Fact]
    public async Task Unauthorized_with_json_body_is_a_typed_error()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.Unauthorized, """{"message":"Unauthorized","statusCode":401}""");

        var error = await Assert.ThrowsAsync<RemnawaveError>(() => client.Api.Users[1].GetAsync(cancellationToken: Ct));
        Assert.True(error.IsUnauthorized);
        Assert.Equal("Unauthorized", error.Message);
    }

    [Fact]
    public async Task Error_without_body_is_a_plain_api_exception()
    {
        // Documented behaviour: a proxy returning an empty 502 cannot be deserialised into RemnawaveError.
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.BadGateway).Respond(HttpStatusCode.BadGateway).Respond(HttpStatusCode.BadGateway).Respond(HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => client.Api.Users[1].GetAsync(cancellationToken: Ct));
        Assert.Equal(502, error.ResponseStatusCode);
    }

    [Fact]
    public async Task Accept_header_is_not_duplicated()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].GetAsync(cancellationToken: Ct);

        Assert.Equal("application/json", stub.Requests[0].Headers["Accept"]);
    }

    [Fact]
    public async Task Token_is_dropped_on_cross_host_redirect()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.Redirect, configure: r => r.Headers.Location = new Uri("https://other.example.net/api/users/42"))
            .Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].GetAsync(cancellationToken: Ct);

        Assert.Equal("Bearer t", stub.Requests[0].Headers["Authorization"]);
        Assert.Equal("other.example.net", stub.Requests[1].Uri.Host);
        Assert.False(stub.Requests[1].Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("my bot")]
    [InlineData("bot/1.0 (+https://example.com)")]
    public void Valid_user_agents_are_accepted(string userAgent) =>
        Assert.NotNull(RemnawaveClientFactory.Create(new RemnawaveClientOptions { BaseUrl = new Uri("https://panel.example.com"), UserAgent = userAgent }));

    [Fact]
    public void Invalid_user_agent_fails_validation_with_clear_message()
    {
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            RemnawaveClientFactory.Create(new RemnawaveClientOptions { BaseUrl = new Uri("https://panel.example.com"), UserAgent = "not a { valid } product" }));
        Assert.Contains("UserAgent", ex.Message);
    }
}
