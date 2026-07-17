using System;
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
        => ContextualTuples is not { Count: > 0 }
            ? null
            : ContextualTuples.Select(tuple => {
                    RejectDefault(tuple);
                    return new ClientTupleKey {
                        User = tuple.User,
                        Relation = tuple.Relation,
                        Object = tuple.Object,
                    };
                }
            ).ToList();

    /// <summary>
    ///     Maps <see cref="ContextualTuples" /> to the SDK shape used by <c>BatchCheck</c> items;
    ///     <c>null</c> when no tuples were supplied.
    /// </summary>
    internal ContextualTupleKeys? ToContextualTupleKeys()
        => ContextualTuples is not { Count: > 0 }
            ? null
            : new ContextualTupleKeys {
                TupleKeys = ContextualTuples.Select(tuple => {
                        RejectDefault(tuple);
                        return new TupleKey {
                            User = tuple.User,
                            Relation = tuple.Relation,
                            Object = tuple.Object,
                        };
                    }
                ).ToList(),
            };

    private static void RejectDefault(SealedFgaContextualTuple tuple) {
        if (tuple.IsDefault) {
            throw new ArgumentException(
                $"A default-constructed {nameof(SealedFgaContextualTuple)} carries no tuple; build "
                + $"entries via {nameof(SealedFgaContextualTuple)}.{nameof(SealedFgaContextualTuple.Of)}(...)."
            );
        }
    }
}
