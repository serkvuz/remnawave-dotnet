namespace Remnawave.Client.Models;

/// <summary>
/// Error returned by the Remnawave panel. Thrown by every request builder for 4XX / 5XX responses.
/// Inspect <see cref="Microsoft.Kiota.Abstractions.ApiException.ResponseStatusCode"/>,
/// <see cref="ErrorCode"/> and, for validation failures (HTTP 400), <see cref="Errors"/>.
/// </summary>
public partial class RemnawaveError
{
    /// <summary>True when the panel reported that the entity does not exist (HTTP 404).</summary>
    public bool IsNotFound => ResponseStatusCode == 404;

    /// <summary>True when the request was rejected by input validation (HTTP 400 with issues).</summary>
    public bool IsValidationError => ResponseStatusCode == 400 && Errors is { Count: > 0 };

    /// <summary>True when the token is missing, invalid or lacks the required scope (HTTP 401 / 403).</summary>
    public bool IsUnauthorized => ResponseStatusCode is 401 or 403;

    /// <inheritdoc />
    public override string ToString() =>
        $"Remnawave API error {ResponseStatusCode}{(ErrorCode is null ? "" : " [" + ErrorCode + "]")}: {Message}";
}
