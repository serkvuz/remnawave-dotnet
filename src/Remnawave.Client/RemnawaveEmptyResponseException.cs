using System;

namespace Remnawave.Client;

/// <summary>Thrown by <c>UnwrapAsync()</c> when the panel answered with success but without a payload.</summary>
public sealed class RemnawaveEmptyResponseException : Exception
{
    /// <summary>Creates the exception for the given envelope type.</summary>
    public RemnawaveEmptyResponseException(string envelopeType)
        : base($"The Remnawave panel returned an empty '{envelopeType}' response.") => EnvelopeType = envelopeType;

    /// <summary>Name of the response envelope model that was empty.</summary>
    public string EnvelopeType { get; }
}
