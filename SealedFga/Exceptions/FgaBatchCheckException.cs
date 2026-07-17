using System;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace SealedFga.Exceptions;

/// <summary>
///     Thrown when a native OpenFGA batch-check response cannot be trusted: an individual item
///     returned an error, or the response does not cover every requested check. Neither case is
///     collapsed to <c>false</c>: a failed or missing existence check would otherwise be
///     indistinguishable from "not allowed" / "tuple does not exist". Not mapped by the exception
///     middleware, so it surfaces as a 500 and lets the outbox drainer retry the originating
///     operation.
/// </summary>
public class FgaBatchCheckException : Exception
{
    /// <summary>
    ///     Thrown when an individual item in a native OpenFGA batch-check returns an error.
    /// </summary>
    public FgaBatchCheckException(ClientBatchCheckItem? request, CheckError? error)
        : base(
            $"Batch check failed for tuple '{request?.User}' / '{request?.Relation}' / '{request?.Object}': "
            + (error?.Message ?? "unknown error")
        ) { }

    /// <summary>
    ///     Thrown when the batch-check response as a whole is malformed (e.g. it does not cover
    ///     every requested correlation ID).
    /// </summary>
    public FgaBatchCheckException(string message) : base(message) { }
}
