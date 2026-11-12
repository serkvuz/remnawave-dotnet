using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Remnawave.Client;

// Placed in the DI namespace so `services.AddRemnawaveClient(...)` is discoverable without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="RemnawaveApiClient"/> with dependency injection.</summary>
public static class RemnawaveServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default Remnawave client, bound to the <c>"Remnawave"</c> configuration section.
    /// </summary>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Remnawave": { "BaseUrl": "https://panel.example.com", "ApiToken": "..." } }
    /// builder.Services.AddRemnawaveClient(builder.Configuration.GetSection("Remnawave"));
    ///
    /// public sealed class MyService(RemnawaveApiClient remnawave) { ... }
    /// </code>
    /// </example>
    /// <returns>The underlying <see cref="IHttpClientBuilder"/>, e.g. to add resilience handlers.</returns>
    public static IHttpClientBuilder AddRemnawaveClient(this IServiceCollection services, IConfiguration configuration) =>
        services.AddRemnawaveClient(Options.Options.DefaultName, configuration);

    /// <summary>Registers the default Remnawave client configured in code.</summary>
    /// <returns>The underlying <see cref="IHttpClientBuilder"/>, e.g. to add resilience handlers.</returns>
    public static IHttpClientBuilder AddRemnawaveClient(this IServiceCollection services, Action<RemnawaveClientOptions> configure) =>
        services.AddRemnawaveClient(Options.Options.DefaultName, configure);

    /// <summary>
    /// Registers a named Remnawave client (for applications talking to several panels).
    /// Resolve it with <c>[FromKeyedServices("name")] RemnawaveApiClient</c> or
    /// <see cref="IRemnawaveClientFactory.CreateClient(string)"/>.
    /// </summary>
    public static IHttpClientBuilder AddRemnawaveClient(this IServiceCollection services, string name, IConfiguration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        services.AddOptions<RemnawaveClientOptions>(name).Configure(options => RemnawaveOptionsBinder.Bind(configuration, options));
        services.AddSingleton<IOptionsChangeTokenSource<RemnawaveClientOptions>>(
            new ConfigurationChangeTokenSource<RemnawaveClientOptions>(name, configuration));
        return services.AddRemnawaveClientCore(name);
    }

    /// <summary>Registers a named Remnawave client configured in code.</summary>
    public static IHttpClientBuilder AddRemnawaveClient(this IServiceCollection services, string name, Action<RemnawaveClientOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        services.AddOptions<RemnawaveClientOptions>(name).Configure(configure);
        return services.AddRemnawaveClientCore(name);
    }

    private static IHttpClientBuilder AddRemnawaveClientCore(this IServiceCollection services, string name)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        name ??= Options.Options.DefaultName;

        services.AddOptions<RemnawaveClientOptions>(name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RemnawaveClientOptions>, RemnawaveClientOptionsValidator>());
        services.TryAddSingleton<IRemnawaveClientFactory, RemnawaveClientFactory>();

        if (string.IsNullOrEmpty(name))
            services.TryAddTransient(sp => sp.GetRequiredService<IRemnawaveClientFactory>().CreateClient(Options.Options.DefaultName));
        else
            services.TryAddKeyedTransient(name, (sp, key) => sp.GetRequiredService<IRemnawaveClientFactory>().CreateClient((string)key!));

        return services
            .AddHttpClient(RemnawaveClientFactory.GetHttpClientName(name))
            .ConfigureHttpClient((sp, http) =>
                RemnawaveClientFactory.ConfigureHttpClient(http, sp.GetRequiredService<IOptionsMonitor<RemnawaveClientOptions>>().Get(name)))
            .ConfigurePrimaryHttpMessageHandler(RemnawaveClientFactory.CreatePrimaryHandler)
            .ConfigureAdditionalHttpMessageHandlers((handlers, sp) =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<RemnawaveClientOptions>>().Get(name);
                foreach (var handler in RemnawaveClientFactory.CreateHandlers(options))
                    handlers.Add(handler);
            });
    }
}
