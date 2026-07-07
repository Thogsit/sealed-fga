using System;
using SealedFga.AuthModel;

namespace SealedFga.Exceptions;

/// <summary>
///     Exception thrown when OpenFGA authorization check fails.
///     Results in HTTP 403 Forbidden response.
/// </summary>
public class FgaForbiddenException : Exception
{
    /// <summary>
    ///     Exception thrown when OpenFGA authorization check fails.
    ///     Results in HTTP 403 Forbidden response.
    /// </summary>
    public FgaForbiddenException(
        ISealedFgaUser user,
        ISealedFgaRelationWithoutAssociatedType relation,
        ISealedFgaTypeIdWithoutAssociatedIdType objectId
    )
        : base($"Access denied: User '{user}' does not have relation '{relation}' to object '{objectId}'") { }

    /// <summary>
    ///     Exception thrown when OpenFGA authorization check fails.
    ///     Results in HTTP 403 Forbidden response.
    /// </summary>
    public FgaForbiddenException(string user, string relation, string objectId)
        : base($"Access denied: User '{user}' does not have relation '{relation}' to object '{objectId}'") { }
}
