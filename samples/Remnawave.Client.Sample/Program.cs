// Remnawave.Client sample: provisions a VPN subscription the way a billing backend would.
//
//   export Remnawave__BaseUrl=https://panel.example.com
//   export Remnawave__ApiToken=<token from Remnawave Settings -> API Tokens>
//   dotnet run --project samples/Remnawave.Client.Sample -- alice
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remnawave.Client;
using Remnawave.Client.Models;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRemnawaveClient(builder.Configuration.GetSection(RemnawaveClientOptions.SectionName));
builder.Services.AddTransient<SubscriptionService>();

using var host = builder.Build();
var service = host.Services.GetRequiredService<SubscriptionService>();

var username = args.FirstOrDefault() ?? "demo-user";
try
{
    var user = await service.ProvisionAsync(username, days: 30, trafficGb: 100);
    Console.WriteLine($"{user.Username}: {user.Status}, expires {user.ExpireAt:u}");
    Console.WriteLine($"Subscription URL: {user.SubscriptionUrl}");
}
catch (RemnawaveError error)
{
    Console.Error.WriteLine(error); // "Remnawave API error 400 [A019]: ..."
    foreach (var issue in error.Errors ?? [])
        Console.Error.WriteLine($"  {string.Join('.', issue.Path ?? [])}: {issue.Message}");
    return 1;
}
return 0;

/// <summary>Example application service: inject <see cref="RemnawaveApiClient"/> like any other dependency.</summary>
internal sealed class SubscriptionService(RemnawaveApiClient remnawave)
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    public async Task<User> ProvisionAsync(string username, int days, int trafficGb, CancellationToken ct = default)
    {
        try
        {
            // Existing customer: extend their subscription.
            var existing = await remnawave.Api.Users.ByUsername[username].GetAsync(cancellationToken: ct).UnwrapAsync();
            return await remnawave.Api.Users.PatchAsync(new UpdateUserBodyDto
            {
                Id = existing.Id,
                Status = UpdateUserStatus.Active,
                ExpireAt = Max(existing.ExpireAt, DateTimeOffset.UtcNow).AddDays(days),
            }, cancellationToken: ct).UnwrapAsync();
        }
        catch (RemnawaveError e) when (e.IsNotFound)
        {
            // New customer: put them in the first internal squad (inbound group).
            var squads = await remnawave.Api.InternalSquads.GetAsync(cancellationToken: ct).UnwrapAsync();
            return await remnawave.Api.Users.PostAsync(new CreateUserBodyDto
            {
                Username = username,
                ExpireAt = DateTimeOffset.UtcNow.AddDays(days),
                TrafficLimitBytes = trafficGb * Gigabyte,
                TrafficLimitStrategy = UserTrafficLimitStrategy.Month,
                ActiveInternalSquads = squads.InternalSquads?.Take(1).Select(s => s.Uuid).ToList(),
            }, cancellationToken: ct).UnwrapAsync();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } v && v > b ? v : b;
}
