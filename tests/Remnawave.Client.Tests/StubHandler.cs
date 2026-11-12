using System.Net;
using System.Text;

namespace Remnawave.Client.Tests;

/// <summary>Records outgoing requests and replays canned responses.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public StubHandler Respond(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            if (json is not null) response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));
        if (_responses.Count == 0) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        var response = _responses.Dequeue()();
        response.RequestMessage = request;
        return response;
    }
}

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
