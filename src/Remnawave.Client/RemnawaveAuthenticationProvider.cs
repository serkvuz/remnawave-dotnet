using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Remnawave.Client;

/// <summary>Adds <c>Authorization: Bearer</c> to requests that target the configured panel host.</summary>
internal sealed class RemnawaveAuthenticationProvider : IAuthenticationProvider
{
    private const string AuthorizationHeader = "Authorization";
    private readonly RemnawaveClientOptions _options;
    private readonly string _host;

    public RemnawaveAuthenticationProvider(RemnawaveClientOptions options)
    {
        _options = options;
        _host = options.BaseUrl?.Host ?? string.Empty;
    }

    public async Task AuthenticateRequestAsync(
        RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Headers.ContainsKey(AuthorizationHeader))
            return;

        // Never leak the token to another host (e.g. an absolute URL passed via WithUrl()).
        if (!string.Equals(request.URI.Host, _host, StringComparison.OrdinalIgnoreCase))
            return;

        var token = await _options.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Add(AuthorizationHeader, "Bearer " + token);
    }
}
