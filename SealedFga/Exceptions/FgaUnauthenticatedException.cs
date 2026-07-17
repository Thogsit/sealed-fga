using System;

namespace SealedFga.Exceptions;

/// <summary>
///     Exception thrown when the current request carries no OpenFGA user claim, so no
///     authorization check can be performed. Results in HTTP 401 Unauthorized response.
/// </summary>
public class FgaUnauthenticatedException(string claimType)
    : Exception($"Request is not authenticated for SealedFGA: no claim of type '{claimType}' was found on the current user");
