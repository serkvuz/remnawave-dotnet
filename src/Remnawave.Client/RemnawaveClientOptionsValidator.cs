using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Remnawave.Client;

/// <summary>Validates <see cref="RemnawaveClientOptions"/> without reflection (trimming/AOT safe).</summary>
internal sealed class RemnawaveClientOptionsValidator : IValidateOptions<RemnawaveClientOptions>
{
    public ValidateOptionsResult Validate(string? name, RemnawaveClientOptions options)
    {
        var failures = new List<string>();
        var label = string.IsNullOrEmpty(name) ? "Remnawave" : $"Remnawave ('{name}')";

        if (options.BaseUrl is null)
            failures.Add($"{label}: BaseUrl is required.");
        else if (!options.BaseUrl.IsAbsoluteUri || (options.BaseUrl.Scheme != Uri.UriSchemeHttp && options.BaseUrl.Scheme != Uri.UriSchemeHttps))
            failures.Add($"{label}: BaseUrl must be an absolute http(s) URL, got '{options.BaseUrl}'.");
        else if (options.BaseUrl.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            failures.Add($"{label}: BaseUrl must be the panel root, without the '/api' suffix (got '{options.BaseUrl}').");

        if (options.Timeout <= TimeSpan.Zero && options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            failures.Add($"{label}: Timeout must be positive.");

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            using var probe = new System.Net.Http.HttpRequestMessage();
            if (!probe.Headers.UserAgent.TryParseAdd(options.UserAgent))
                failures.Add($"{label}: UserAgent '{options.UserAgent}' is not a valid User-Agent value (expected e.g. 'my-app/1.0').");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
