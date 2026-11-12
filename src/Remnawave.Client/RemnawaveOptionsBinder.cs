using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Remnawave.Client;

/// <summary>Reflection-free configuration binding (trimming / Native AOT safe on every target framework).</summary>
internal static class RemnawaveOptionsBinder
{
    public static void Bind(IConfiguration configuration, RemnawaveClientOptions options)
    {
        if (configuration[nameof(RemnawaveClientOptions.BaseUrl)] is { Length: > 0 } baseUrl)
        {
            options.BaseUrl = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                ? uri
                : throw new FormatException($"Remnawave:BaseUrl '{baseUrl}' is not an absolute URL.");
        }

        if (configuration[nameof(RemnawaveClientOptions.ApiToken)] is { Length: > 0 } token)
            options.ApiToken = token;

        if (configuration[nameof(RemnawaveClientOptions.Timeout)] is { Length: > 0 } timeout)
        {
            options.Timeout = TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var ts)
                ? ts
                : double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : throw new FormatException($"Remnawave:Timeout '{timeout}' must be a TimeSpan (00:00:30) or seconds.");
        }

        if (configuration[nameof(RemnawaveClientOptions.UseForwardedHeaders)] is { Length: > 0 } forwarded)
        {
            options.UseForwardedHeaders = bool.TryParse(forwarded, out var b)
                ? b
                : throw new FormatException($"Remnawave:UseForwardedHeaders '{forwarded}' must be true or false.");
        }

        if (configuration[nameof(RemnawaveClientOptions.UserAgent)] is { Length: > 0 } userAgent)
            options.UserAgent = userAgent;

        foreach (var header in configuration.GetSection(nameof(RemnawaveClientOptions.DefaultHeaders)).GetChildren())
        {
            if (header.Value is not null)
                options.DefaultHeaders[header.Key] = header.Value;
        }
    }
}
