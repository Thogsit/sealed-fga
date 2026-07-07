using System;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace SealedFga.Exceptions;

/// <summary>
///     Thrown when an individual item in a native OpenFGA batch-check returns an error rather than
///     a boolean result. It is deliberately <b>not</b> collapsed to <c>false</c>: a failed
///     existence check would otherwise be indistinguishable from "tuple does not exist" and would
///     corrupt the idempotent write/delete logic. Not mapped by the exception middleware, so it
///     surfaces as a 500 and lets the outbox drainer retry the originating operation.
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
}
