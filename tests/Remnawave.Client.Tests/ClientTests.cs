using System.Net;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Remnawave.Client.Models;
using Xunit;

namespace Remnawave.Client.Tests;

public sealed class ClientTests
{
    private static readonly Uri Panel = new("https://panel.example.com");

    private static (RemnawaveApiClient Client, StubHandler Stub) Create(Action<RemnawaveClientOptions>? configure = null)
    {
        var stub = new StubHandler();
        var options = new RemnawaveClientOptions { BaseUrl = Panel, ApiToken = "secret-token" };
        configure?.Invoke(options);
        return (RemnawaveClientFactory.Create(options, stub), stub);
    }

    [Fact]
    public async Task Get_user_unwraps_envelope_and_maps_types()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        User user = await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken).UnwrapAsync();

        Assert.Equal(42L, user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(UserTrafficLimitStrategy.MonthRolling, user.TrafficLimitStrategy);
        Assert.Equal(107374182400L, user.TrafficLimitBytes);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero), user.ExpireAt);
        Assert.Equal(123456789L, user.TelegramId);
        Assert.Equal(5368709120L, user.UserTraffic!.UsedTrafficBytes);
        Assert.Equal("Default", Assert.Single(user.ActiveInternalSquads!).Name);
        Assert.Equal("kept in AdditionalData", user.AdditionalData["someFutureField"]?.ToString());

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://panel.example.com/api/users/42", request.Uri.ToString());
        Assert.Equal("Bearer secret-token", request.Headers["Authorization"]);
        Assert.Contains("Remnawave.Client/", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Create_user_serialises_dates_as_utc_z_and_enums_as_wire_values()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.Created, Fixtures.UserJson);

        await client.Api.Users.PostAsync(new CreateUserBodyDto
        {
            Username = "alice",
            Status = UserStatus.Active,
            TrafficLimitStrategy = UserTrafficLimitStrategy.MonthRolling,
            TrafficLimitBytes = 107374182400,
            ExpireAt = new DateTimeOffset(2026, 12, 31, 23, 59, 59, 500, TimeSpan.FromHours(2)),
            ActiveInternalSquads = [Guid.Parse("4d3b0d7e-0e7d-4c84-8f43-0c8b8a7d3a10")],
        }, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("2026-12-31T21:59:59.500Z", root.GetProperty("expireAt").GetString());
        Assert.Equal("ACTIVE", root.GetProperty("status").GetString());
        Assert.Equal("MONTH_ROLLING", root.GetProperty("trafficLimitStrategy").GetString());
        Assert.Equal(107374182400L, root.GetProperty("trafficLimitBytes").GetInt64());
        Assert.False(root.TryGetProperty("description", out _), "unset properties must not be sent");
    }

    [Fact]
    public async Task Patch_only_sends_assigned_properties()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users.PatchAsync(new UpdateUserBodyDto { Id = 42, Description = "vip" },
            cancellationToken: TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(stub.Requests[0].Body!);
        Assert.Equal(["description", "id"], body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task Not_found_throws_typed_error()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.NotFound, """{"timestamp":"2026-01-01T00:00:00.000Z","path":"/api/users/7","message":"User not found","errorCode":"A063"}""");

        var error = await Assert.ThrowsAsync<RemnawaveError>(() => client.Api.Users[7].GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.IsNotFound);
        Assert.Equal(404, error.ResponseStatusCode);
        Assert.Equal("User not found", error.Message);
        Assert.Equal("A063", error.ErrorCode);
        Assert.Equal("/api/users/7", error.Path);
    }

    [Fact]
    public async Task Validation_error_exposes_issues()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.BadRequest, """
            {"message":"Validation failed","statusCode":400,
             "errors":[{"validation":"regex","code":"invalid_string","message":"Invalid username","path":["username"]}]}
            """);

        var error = await Assert.ThrowsAsync<RemnawaveError>(() =>
            client.Api.Users.PostAsync(new CreateUserBodyDto { Username = "!" }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.IsValidationError);
        var issue = Assert.Single(error.Errors!);
        Assert.Equal("username", Assert.Single(issue.Path!));
        Assert.Equal("invalid_string", issue.Code);
    }

    [Fact]
    public async Task Unauthorized_without_body_still_throws_api_exception()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => client.Api.Users[1].GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(401, error.ResponseStatusCode);
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.ServiceUnavailable, configure: r => r.Headers.Add("Retry-After", "0"))
            .Respond(HttpStatusCode.OK, Fixtures.UserJson);

        var user = await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken).UnwrapAsync();

        Assert.Equal("alice", user.Username);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Forwarded_and_default_headers_are_applied()
    {
        var (client, stub) = Create(o =>
        {
            o.BaseUrl = new Uri("http://remnawave:3000");
            o.UseForwardedHeaders = true;
            o.DefaultHeaders["X-Api-Key"] = "proxy-secret";
            o.UserAgent = "sericavpn-backend/1.0";
        });
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        var headers = stub.Requests[0].Headers;
        Assert.Equal("https", headers["X-Forwarded-Proto"]);
        Assert.Equal("127.0.0.1", headers["X-Forwarded-For"]);
        Assert.Equal("proxy-secret", headers["X-Api-Key"]);
        Assert.Contains("sericavpn-backend/1.0", headers["User-Agent"]);
    }

    [Fact]
    public async Task Base_url_with_sub_path_and_trailing_slash_is_respected()
    {
        var (client, stub) = Create(o => o.BaseUrl = new Uri("https://example.com/panel/"));
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/panel/api/users/42", stub.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Token_is_never_sent_to_another_host()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].WithUrl("https://attacker.example.net/api/users/42").GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(stub.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Token_provider_is_used_per_request()
    {
        var calls = 0;
        var (client, stub) = Create(o => o.TokenProvider = _ => Task.FromResult<string?>($"jwt-{++calls}"));
        stub.Respond(HttpStatusCode.OK, Fixtures.UserJson).Respond(HttpStatusCode.OK, Fixtures.UserJson);

        await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer jwt-1", stub.Requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer jwt-2", stub.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Empty_envelope_throws_descriptive_exception()
    {
        var (client, stub) = Create();
        stub.Respond(HttpStatusCode.OK, "{}");

        var ex = await Assert.ThrowsAsync<RemnawaveEmptyResponseException>(() =>
            client.Api.Users[42].GetAsync(cancellationToken: TestContext.Current.CancellationToken).UnwrapAsync());
        Assert.Equal("UserResponseDto", ex.EnvelopeType);
    }

    [Theory]
    [InlineData("https://panel.example.com/api")]
    [InlineData("ftp://panel.example.com")]
    public void Invalid_base_url_is_rejected(string url)
    {
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            RemnawaveClientFactory.Create(new RemnawaveClientOptions { BaseUrl = new Uri(url) }));
    }
}
