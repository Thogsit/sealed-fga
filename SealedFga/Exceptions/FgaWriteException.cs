using System;
using System.Collections.Generic;
using System.Linq;
using OpenFga.Sdk.Client.Model;

namespace SealedFga.Exceptions;

/// <summary>
///     Thrown when a non-transactional OpenFGA write reports one or more per-tuple failures.
///     The SDK does not throw in non-transactional mode — it returns a per-tuple status — so a
///     failed chunk would otherwise be silently swallowed and (e.g.) an outbox row marked as
///     processed even though its tuple never reached the store. Not mapped by the exception
///     middleware, so it surfaces as a 500 and lets the outbox drainer retry the originating
///     operation.
/// </summary>
public class FgaWriteException : Exception
{
    /// <summary>
    ///     Creates the exception from the failed items of a <c>ClientWriteResponse</c>.
    /// </summary>
    /// <param name="failures">The response items whose status is not a success.</param>
    public FgaWriteException(IReadOnlyList<ClientWriteSingleResponse> failures)
        : base(BuildMessage(failures), failures.Select(f => f.Error).FirstOrDefault(e => e != null)) { }

    private static string BuildMessage(IReadOnlyList<ClientWriteSingleResponse> failures) {
        var tuples = string.Join(
            "; ",
            failures.Select(f =>
                $"'{f.TupleKey.User}' / '{f.TupleKey.Relation}' / '{f.TupleKey.Object}'"
            )
        );
        var firstError = failures.Select(f => f.Error?.Message).FirstOrDefault(m => m != null);
        return $"OpenFGA write failed for {failures.Count} tuple(s): {tuples}"
               + (firstError != null ? $" — first error: {firstError}" : "");
    }
}
