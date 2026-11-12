using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace Remnawave.Client;

/// <summary>Creates <see cref="RemnawaveApiClient"/> instances for named panels registered in DI.</summary>
public interface IRemnawaveClientFactory
{
    /// <summary>Creates a client for the panel registered under <paramref name="name"/>.</summary>
    /// <param name="name">Name passed to <c>AddRemnawaveClient(name, ...)</c>; empty for the default panel.</param>
    RemnawaveApiClient CreateClient(string name);
}

/// <summary>
/// Entry point for creating Remnawave clients, with or without dependency injection.
/// </summary>
public sealed class RemnawaveClientFactory : IRemnawaveClientFactory
{
    internal const string HttpClientNamePrefix = "Remnawave.Client";

    private static readonly string LibraryUserAgent =
        "Remnawave.Client/" + (typeof(RemnawaveClientFactory).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<RemnawaveClientOptions> _options;

    /// <summary>Used by dependency injection.</summary>
    public RemnawaveClientFactory(IHttpClientFactory httpClientFactory, IOptionsMonitor<RemnawaveClientOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    /// <inheritdoc />
    public RemnawaveApiClient CreateClient(string name)
    {
        var options = _options.Get(name);
        var httpClient = _httpClientFactory.CreateClient(GetHttpClientName(name));
        return CreateClient(options, httpClient);
    }

    /// <summary>
    /// Creates a standalone client (no dependency injection). The returned client is thread-safe:
    /// create it once and reuse it for the lifetime of your application.
    /// </summary>
    /// <example>
    /// <code>
    /// var client = RemnawaveClientFactory.Create(new Uri("https://panel.example.com"), "api-token");
    /// var users = await client.Api.Users.GetAsync().UnwrapAsync();
    /// </code>
    /// </example>
    public static RemnawaveApiClient Create(Uri baseUrl, string? apiToken) =>
        Create(new RemnawaveClientOptions { BaseUrl = baseUrl, ApiToken = apiToken });

    /// <summary>Creates a standalone client from <paramref name="options"/>. Reuse the returned instance.</summary>
    /// <param name="options">Connection settings.</param>
    /// <param name="innerHandler">Optional transport handler (proxy, custom certificate validation, tests).</param>
    public static RemnawaveApiClient Create(RemnawaveClientOptions options, HttpMessageHandler? innerHandler = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var validation = new RemnawaveClientOptionsValidator().Validate(null, options);
        if (validation.Failed) throw new OptionsValidationException(Options.DefaultName, typeof(RemnawaveClientOptions), validation.Failures);

        var handlers = CreateHandlers(options);
        var httpClient = KiotaClientFactory.Create(handlers, innerHandler ?? CreatePrimaryHandler());
        ConfigureHttpClient(httpClient, options);
        return CreateClient(options, httpClient);
    }

    internal static RemnawaveApiClient CreateClient(RemnawaveClientOptions options, HttpClient httpClient)
    {
        var adapter = new HttpClientRequestAdapter(
            new RemnawaveAuthenticationProvider(options),
            RemnawaveJsonSerialization.CreateParseNodeFactory(),
            RemnawaveJsonSerialization.CreateSerializationWriterFactory(),
            httpClient)
        {
            BaseUrl = options.GetNormalizedBaseUrl(),
        };
        return new RemnawaveApiClient(adapter);
    }

    internal static string GetHttpClientName(string name) =>
        string.IsNullOrEmpty(name) ? HttpClientNamePrefix : HttpClientNamePrefix + ":" + name;

    /// <summary>Kiota middleware (retry with Retry-After, redirects, parameter decoding, ...) + Remnawave headers.</summary>
    internal static IList<DelegatingHandler> CreateHandlers(RemnawaveClientOptions options)
    {
        var handlers = KiotaClientFactory.CreateDefaultHandlers();
        handlers.Add(new RemnawaveDefaultHeadersHandler(options));
        return handlers;
    }

    internal static HttpMessageHandler CreatePrimaryHandler()
    {
        var handler = KiotaClientFactory.GetDefaultHttpMessageHandler();
#if NET8_0_OR_GREATER
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AutomaticDecompression = DecompressionMethods.All;
            sockets.PooledConnectionLifetime = TimeSpan.FromMinutes(5); // pick up DNS changes for long-lived standalone clients
        }
#else
        if (handler is HttpClientHandler classic && classic.SupportsAutomaticDecompression)
            classic.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
#endif
        return handler;
    }

    internal static void ConfigureHttpClient(HttpClient httpClient, RemnawaveClientOptions options)
    {
        httpClient.Timeout = options.Timeout;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(LibraryUserAgent);
        if (!string.IsNullOrWhiteSpace(options.UserAgent))
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
