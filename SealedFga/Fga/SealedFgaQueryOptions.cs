using System.Collections.Generic;
using System.Linq;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;

namespace SealedFga.Fga;

/// <summary>
///     Optional per-call options for <see cref="SealedFgaService" /> check/list operations
///     (<see cref="SealedFgaService.CheckAsync{TObjId}" />, <see cref="SealedFgaService.EnsureCheckAsync{TObjId}" />,
///     <see cref="SealedFgaService.ListObjectsAsync{TObjId}" /> and <see cref="SealedFgaService.BatchCheckAsync{TObjId}" />).
///     Omitting the options (or leaving a property <c>null</c>) keeps OpenFGA's default behavior.
/// </summary>
public sealed class SealedFgaQueryOptions {
    /// <summary>
    ///     The read consistency for this call. <see cref="ConsistencyPreference.HIGHERCONSISTENCY" />
    ///     trades latency for evaluating against the freshest state; <c>null</c> leaves the choice to
    ///     the server (its default is to minimize latency).
    /// </summary>
    public ConsistencyPreference? Consistency { get; init; }

    /// <summary>
    ///     Contextual tuples evaluated by OpenFGA as if they were stored, for this call only.
    ///     Build entries via <see cref="SealedFgaContextualTuple.Of{TObjId}" />.
    /// </summary>
    public IReadOnlyCollection<SealedFgaContextualTuple>? ContextualTuples { get; init; }

    /// <summary>
    ///     Maps <see cref="ContextualTuples" /> to the SDK shape used by <c>Check</c> and
    ///     <c>ListObjects</c> request bodies; <c>null</c> when no tuples were supplied.
    /// </summary>
    internal List<ClientTupleKey>? ToClientTupleKeys()
        => SealedFgaContextualTuple.ToClientTupleKeys(ContextualTuples);

    /// <summary>
    ///     Maps <see cref="ContextualTuples" /> to the SDK shape used by <c>BatchCheck</c> items;
    ///     <c>null</c> when no tuples were supplied.
    /// </summary>
    internal ContextualTupleKeys? ToContextualTupleKeys()
        => SealedFgaContextualTuple.ToContextualTupleKeys(ContextualTuples);

    /// <summary>
    ///     Merges ambient options (from an <see cref="ISealedFgaAmbientOptionsProvider" />) with the
    ///     explicit per-call options for a single concrete-object operation:
    ///     <see cref="ContextualTuples" /> are unioned (both sets are sent), while an explicit per-call
    ///     <see cref="Consistency" /> wins over the ambient one. Returns <see cref="Object.ReferenceEquals" />-
    ///     identical <paramref name="perCall" /> when <paramref name="ambient" /> contributes nothing (and
    ///     vice-versa), and <c>null</c> when neither carries anything — preserving the no-provider fast path.
    /// </summary>
    internal static SealedFgaQueryOptions? Merge(SealedFgaQueryOptions? ambient, SealedFgaQueryOptions? perCall) {
        if (ambient is null) {
            return perCall;
        }

        if (perCall is null) {
            return ambient;
        }

        // Union the two tuple sets, dropping empties so the result stays null when neither has any.
        var ambientTuples = ambient.ContextualTuples;
        var perCallTuples = perCall.ContextualTuples;
        IReadOnlyCollection<SealedFgaContextualTuple>? mergedTuples;
        if (ambientTuples is not { Count: > 0 }) {
            mergedTuples = perCallTuples;
        } else if (perCallTuples is not { Count: > 0 }) {
            mergedTuples = ambientTuples;
        } else {
            mergedTuples = ambientTuples.Concat(perCallTuples).ToList();
        }

        return new SealedFgaQueryOptions {
            // Explicit per-call consistency wins; else the ambient one; else null.
            Consistency = perCall.Consistency ?? ambient.Consistency,
            ContextualTuples = mergedTuples,
        };
    }
}
