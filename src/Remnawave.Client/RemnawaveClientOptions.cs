using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Remnawave.Client;

/// <summary>Connection settings for a Remnawave panel.</summary>
/// <remarks>Bind from configuration (section <see cref="SectionName"/>) or configure in code.</remarks>
public sealed class RemnawaveClientOptions
{
    /// <summary>Default configuration section name: <c>"Remnawave"</c>.</summary>
    public const string SectionName = "Remnawave";

    /// <summary>Root URL of the panel, e.g. <c>https://panel.example.com</c> (without the <c>/api</c> suffix).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// API token created in the panel under <em>Remnawave Settings → API Tokens</em>. Sent as
    /// <c>Authorization: Bearer &lt;token&gt;</c>. Leave empty when only calling public endpoints.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Optional dynamic token source (e.g. a JWT from <c>/api/auth/login</c> or a secret store).
    /// Takes precedence over <see cref="ApiToken"/> when set.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? TokenProvider { get; set; }

    /// <summary>Per-request timeout. Defaults to 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds <c>X-Forwarded-Proto: https</c> and <c>X-Forwarded-For: 127.0.0.1</c> to every request.
    /// Remnawave rejects plain-HTTP API calls that did not come through a reverse proxy, so enable
    /// this when you reach the panel directly over HTTP (e.g. <c>http://remnawave:3000</c> inside a
    /// Docker network).
    /// </summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>
    /// Extra headers sent with every request, e.g. a reverse-proxy secret
    /// (<c>X-Api-Key</c>) or a <c>Cookie</c> required by a panel protected with caddy-security.
    /// </summary>
    public IDictionary<string, string> DefaultHeaders { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Value appended to the <c>User-Agent</c> header (e.g. <c>"my-bot/1.2"</c>).
    /// </summary>
    public string? UserAgent { get; set; }

    internal async Task<string?> GetTokenAsync(CancellationToken cancellationToken) =>
        TokenProvider is { } provider ? await provider(cancellationToken).ConfigureAwait(false) : ApiToken;

    internal string GetNormalizedBaseUrl() =>
        (BaseUrl ?? throw new InvalidOperationException("RemnawaveClientOptions.BaseUrl is required."))
            .GetLeftPart(UriPartial.Path).TrimEnd('/');
}
