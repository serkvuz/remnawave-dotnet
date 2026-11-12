using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Remnawave.Client;

/// <summary>Applies <see cref="RemnawaveClientOptions.DefaultHeaders"/> and forwarded headers.</summary>
internal sealed class RemnawaveDefaultHeadersHandler : DelegatingHandler
{
    private readonly RemnawaveClientOptions _options;

    public RemnawaveDefaultHeadersHandler(RemnawaveClientOptions options) => _options = options;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.UseForwardedHeaders)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        }

        foreach (var header in _options.DefaultHeaders)
        {
            if (!request.Headers.Contains(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
