# Remnawave.Client

Strongly-typed .NET client for the [Remnawave](https://docs.rw) panel API, **generated from the official OpenAPI document**.

- **Complete and always current.** Every endpoint and model comes from `https://cdn.docs.rw/docs/openapi.json`. A scheduled workflow regenerates the client when Remnawave publishes a new version.
- **Idiomatic C#.** Inline schemas are deduplicated into clean types such as `User`, `Node`, `InternalSquad`, `UserStatus.Active`, `long` byte counters and `DateTimeOffset` dates. You don't get `UserResponseDto_response_activeInternalSquads`.
- **Fast and lean.** No reflection: it uses System.Text.Json source generation through [Kiota](https://learn.microsoft.com/openapi/kiota/), and it is **trimming and Native AOT compatible** (verified in CI). It reuses connections through `IHttpClientFactory`, retries on 429/503 honouring `Retry-After`, and handles gzip/brotli.
- **Easy to wire up.** One `AddRemnawaveClient()` call, configuration binding, multiple panels, typed errors.
- Targets **.NET 8, .NET 10 and .NET Standard 2.0**.

> This is an unofficial, community client. It is not affiliated with the Remnawave project.

## Install

```sh
dotnet add package Remnawave.Client
```

## Quick start

### With dependency injection (ASP.NET Core, Worker Service, bots…)

```jsonc
// appsettings.json  (keep the token in user-secrets / environment: Remnawave__ApiToken)
{
  "Remnawave": {
    "BaseUrl": "https://panel.example.com",
    "ApiToken": "<Remnawave Settings → API Tokens>"
  }
}
```

```csharp
builder.Services.AddRemnawaveClient(builder.Configuration.GetSection("Remnawave"));
```

```csharp
using Remnawave.Client;
using Remnawave.Client.Models;

public sealed class SubscriptionService(RemnawaveApiClient remnawave)
{
    public async Task<string?> GetSubscriptionUrlAsync(string username, CancellationToken ct)
    {
        User user = await remnawave.Api.Users.ByUsername[username].GetAsync(cancellationToken: ct).UnwrapAsync();
        return user.SubscriptionUrl;
    }
}
```

### Without dependency injection

```csharp
var remnawave = RemnawaveClientFactory.Create(new Uri("https://panel.example.com"), "<api token>");
// The client is thread-safe: create it once and reuse it.
```

## Usage

The API surface mirrors the REST paths: `/api/users/{id}/actions/disable` becomes `remnawave.Api.Users[id].Actions.Disable.PostAsync()`. Every response is wrapped by the panel in `{ "response": ... }`. Call `.UnwrapAsync()` to get the payload directly.

```csharp
// Create a user
User user = await remnawave.Api.Users.PostAsync(new CreateUserBodyDto
{
    Username = "alice",
    ExpireAt = DateTimeOffset.UtcNow.AddDays(30),
    TrafficLimitBytes = 100L * 1024 * 1024 * 1024,           // 100 GiB
    TrafficLimitStrategy = UserTrafficLimitStrategy.Month,
    ActiveInternalSquads = [squadUuid],
}).UnwrapAsync();

// Read
User byId       = await remnawave.Api.Users[user.Id!.Value].GetAsync().UnwrapAsync();
User byUsername = await remnawave.Api.Users.ByUsername["alice"].GetAsync().UnwrapAsync();

// Page through users
UsersResult page = await remnawave.Api.Users.GetAsync(r =>
{
    r.QueryParameters.Start = 0;
    r.QueryParameters.Size = 100;
}).UnwrapAsync();

// Update: only the properties you set are sent
await remnawave.Api.Users.PatchAsync(new UpdateUserBodyDto { Id = user.Id, Description = "VIP" });

// Actions
await remnawave.Api.Users[user.Id!.Value].Actions.Disable.PostAsync();
await remnawave.Api.Users[user.Id!.Value].Actions.ResetTraffic.PostAsync();

// Nodes, squads, system
List<Node> nodes             = await remnawave.Api.Nodes.GetAsync().UnwrapAsync();
InternalSquadsResult squads  = await remnawave.Api.InternalSquads.GetAsync().UnwrapAsync();
SystemStats stats            = await remnawave.Api.System.Stats.GetAsync().UnwrapAsync();
```

Every method accepts a `CancellationToken`. Unknown fields returned by newer panel versions are kept in each model's `AdditionalData`, so an older client never breaks.

### Errors

Any 4XX/5XX response with a JSON body (every error the panel itself returns) throws `RemnawaveError`, which derives from Kiota's `ApiException`. An error without a body, such as an empty 502 from a reverse proxy, throws a plain `ApiException`, so catch that as well if you sit behind a proxy:

```csharp
try
{
    await remnawave.Api.Users.ByUsername["bob"].GetAsync().UnwrapAsync();
}
catch (RemnawaveError e) when (e.IsNotFound)
{
    // 404: create the user instead
}
catch (RemnawaveError e) when (e.IsValidationError)
{
    foreach (var issue in e.Errors!)
        logger.LogWarning("{Field}: {Message}", string.Join('.', issue.Path!), issue.Message);
}
catch (RemnawaveError e)
{
    logger.LogError("{Status} {Code}: {Message}", e.ResponseStatusCode, e.ErrorCode, e.Message);
}
```

## Configuration

| Option | Default | Description |
| --- | --- | --- |
| `BaseUrl` | (required) | Panel root URL, without `/api`. Sub-paths are supported (`https://example.com/panel`). |
| `ApiToken` | | Bearer token. It is only ever sent to the `BaseUrl` host. |
| `TokenProvider` | | `Func<CancellationToken, Task<string?>>` for dynamic tokens (secret store, `/api/auth/login` JWT). It overrides `ApiToken`. |
| `Timeout` | `00:00:30` | Per-request timeout. |
| `UseForwardedHeaders` | `false` | Adds `X-Forwarded-Proto: https` and `X-Forwarded-For: 127.0.0.1`. **Enable this when you call the panel directly over HTTP** (for example `http://remnawave:3000` inside a Docker network), because Remnawave rejects plain-HTTP requests that didn't come through a reverse proxy. |
| `DefaultHeaders` | | Extra headers for every request, such as a reverse-proxy secret or a caddy-security cookie. |
| `UserAgent` | | Appended to the `User-Agent` header. |

### Several panels

```csharp
services.AddRemnawaveClient("a", configuration.GetSection("Remnawave:A"));
services.AddRemnawaveClient("b", configuration.GetSection("Remnawave:B"));

public sealed class Sync([FromKeyedServices("a")] RemnawaveApiClient a, IRemnawaveClientFactory panels)
{
    RemnawaveApiClient b = panels.CreateClient("b");
}
```

### Resilience, logging and telemetry

`AddRemnawaveClient` returns the `IHttpClientBuilder`, so the whole `Microsoft.Extensions.Http` ecosystem plugs in:

```csharp
builder.Services.AddRemnawaveClient(builder.Configuration.GetSection("Remnawave"))
    .AddStandardResilienceHandler();   // Microsoft.Extensions.Http.Resilience (Polly)
```

## Versioning

The package version follows the Remnawave API version it was generated from. `3.4.10` targets panel v3.4.10, and a client-only fix ships as `3.4.10.1`. The targeted API version is also stamped into the assembly as the `RemnawaveApiVersion` metadata.

## How it is built

```
spec/remnawave-openapi.json             official document, downloaded from docs.rw
        │  scripts/Preprocess.cs        normalise, deduplicate, name types, fix typing
spec/remnawave-openapi.processed.json
        │  Kiota (pinned in .config/dotnet-tools.json)
src/Remnawave.Client/Generated/         generated request builders + models (do not edit)
        │  scripts/GenerateExtensions.cs
src/Remnawave.Client/RemnawaveResponseExtensions.g.cs   UnwrapAsync() helpers
src/Remnawave.Client/*.cs               hand-written runtime: DI, auth, options, errors, JSON
```

Without preprocessing, the upstream document generates poorly. The preprocessor:

1. **Deduplicates** structurally identical inline schemas (884 inline occurrences become 321 shared types) and names them from their location, for example `UserResponseDto.response` becomes `User`. You can adjust names in `spec/schema-names.json`.
2. **Merges** the four error DTOs into a single `RemnawaveError` and maps it to 4XX/5XX. The upstream document declares only 400/404/500, so 401/403 were untyped.
3. **Fixes typing.** Integral `number`s become `long` (IDs, byte counters, counts, paging), body dates become `DateTimeOffset`, primitive unions collapse, and recursive "any JSON" schemas become free-form `UntypedNode`.
4. **Removes `default` values.** Kiota assigns them in constructors, which would silently overwrite fields in PATCH requests.
5. Adds PascalCase enum member names (`MONTH_ROLLING` becomes `MonthRolling`).

At runtime, dates are serialised as `2025-01-17T15:38:45.065Z`. The panel's validators reject Kiota's default `+00:00` form.

### Regenerating

```sh
./scripts/generate.sh             # from the committed spec
./scripts/generate.sh --download  # fetch the latest spec from docs.rw first
dotnet test
```

CI fails if the committed generated code differs from what the scripts produce. The **Update Remnawave API** workflow runs daily and opens a PR when docs.rw publishes a new version. Merge it and push a tag `v<version>` to publish to NuGet.

## License

MIT. Remnawave itself is licensed under AGPL-3.0.
