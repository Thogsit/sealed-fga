using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFga.Sdk.Model;
using SealedFga.AuthModel;
using SealedFga.Exceptions;

namespace SealedFga.Fga;

/// <summary>
///     The strongly-typed OpenFGA client surface of <see cref="SealedFgaService" />. Consumers
///     should depend on this interface (registered scoped alongside the implementation by
///     <c>ConfigureSealedFga</c>) so authorization calls can be substituted in tests.
/// </summary>
public interface ISealedFgaService {
    /// <summary>
    ///     Deletes all relations that contain the given ID as a User or Object.
    /// </summary>
    /// <param name="objId">The ID to fully delete all related relations of.</param>
    /// <typeparam name="TObjId">The type of the ID.</typeparam>
    Task DeleteObjectFromOpenFgaIncludingAllRelations<TObjId>(TObjId objId)
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Deletes every <b>stored</b> relationship tuple in which the given raw object appears, either
    ///     as the object or as the user/subject. Used by the outbox drainer to purge a deleted entity.
    /// </summary>
    /// <param name="rawObjectId">The object's OpenFGA tuple string (<c>type:id</c>).</param>
    /// <param name="typeName">The object's OpenFGA type name.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    Task DeleteAllRelationsForRawObjectAsync(
        string rawObjectId,
        string typeName,
        CancellationToken cancellationToken = new()
    );

    /// <summary>
    ///     Ensures authorization using strongly typed IDs, throwing an exception if the check fails.
    /// </summary>
    /// <param name="user">The user ID (strongly typed)</param>
    /// <param name="relation">The relation string</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <exception cref="FgaForbiddenException">Thrown when the authorization check fails</exception>
    /// <returns>A task that represents the asynchronous operation</returns>
    Task EnsureCheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Checks authorization using strongly typed IDs.
    /// </summary>
    /// <param name="user">The user ID (strongly typed)</param>
    /// <param name="relation">The relation string</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>True if the relation exists, false otherwise</returns>
    Task<bool> CheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Lists objects that a user has a specific relation to, returning strongly typed IDs.
    /// </summary>
    /// <param name="user">The user to check (strongly typed)</param>
    /// <param name="relation">The relation to check</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>List of strongly typed object IDs</returns>
    Task<IEnumerable<TObjId>> ListObjectsAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Lists the subjects of a given user type that have a relation to an object, returning
    ///     strongly typed IDs. Single-shot: OpenFGA's <c>ListUsers</c> is not paginated (no
    ///     continuation token), so the response is complete.
    /// </summary>
    /// <param name="objectId">The object whose subjects are listed (strongly typed)</param>
    /// <param name="relation">The relation to check (bound to the object type)</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <typeparam name="TUserId">The user ID type to list (the user-type filter)</typeparam>
    /// <returns>
    ///     A <see cref="SealedFgaListUsersResult{TUserId}" /> exposing concrete subjects, userset
    ///     subjects, and whether a <c>type:*</c> wildcard was returned — each surfaced explicitly so
    ///     a wildcard ("every user of this type") is never silently dropped.
    /// </returns>
    Task<SealedFgaListUsersResult<TUserId>> ListUsersAsync<TObjId, TUserId>(
        TObjId objectId,
        ISealedFgaRelation<TObjId> relation,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>
        where TUserId : ISealedFgaTypeId<TUserId>;

    /// <summary>
    ///     Lists which of the given relations a user has to a specific object, returning the matching
    ///     strongly typed relations. Implemented as a single batch check (not the SDK's
    ///     <c>ListRelations</c>) so it inherits the strict, fail-loud mapping contract: an incomplete
    ///     or errored response throws rather than silently reporting a relation as absent.
    /// </summary>
    /// <param name="user">The user to check (strongly typed)</param>
    /// <param name="relations">The candidate relations to test (bound to the object type)</param>
    /// <param name="objectId">The object to check against (strongly typed)</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>The subset of <paramref name="relations" /> the user holds, in input order.</returns>
    Task<IReadOnlyList<ISealedFgaRelation<TObjId>>> ListRelationsAsync<TObjId>(
        ISealedFgaUser user,
        IEnumerable<ISealedFgaRelation<TObjId>> relations,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Performs batch check operations using strongly typed IDs. One shared
    ///     <paramref name="queryOptions" /> set (contextual tuples + consistency) applies to every item.
    /// </summary>
    /// <param name="checks">List of check requests with strongly typed IDs</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency), applied to every check in the batch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>Dictionary with results for each check</returns>
    Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
        BatchCheckAsync<TObjId>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Performs batch check operations using strongly typed IDs, where each check carries its
    ///     <b>own</b> contextual tuples produced by <paramref name="contextualTuplesFactory" /> — the
    ///     shape needed for per-object request-time tuples (e.g. a super-user grant attached to each
    ///     object individually). <paramref name="consistency" /> applies to the whole batch. The
    ///     fail-loud mapping contract is unchanged.
    /// </summary>
    /// <param name="checks">List of check requests with strongly typed IDs</param>
    /// <param name="contextualTuplesFactory">
    ///     Returns the contextual tuples for a single check (or <c>null</c>/empty for none). Invoked
    ///     once per check, in input order.
    /// </param>
    /// <param name="consistency">Optional read consistency applied to the whole batch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>Dictionary with results for each check</returns>
    Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
        BatchCheckAsync<TObjId>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            Func<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object),
                IReadOnlyCollection<SealedFgaContextualTuple>?> contextualTuplesFactory,
            ConsistencyPreference? consistency = null,
            CancellationToken cancellationToken = new()
        )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Writes one strongly-typed tuple to OpenFGA. Idempotent server-side: an already-stored
    ///     tuple is ignored (<c>OnDuplicateWrites = Ignore</c>) rather than failing, and — unlike a
    ///     check-then-write — the tuple is <b>always</b> materialized even when a computed relation
    ///     (e.g. a union arm) already grants the same access. Thin wrapper over
    ///     <see cref="WriteAsync(IReadOnlyCollection{SealedFgaTupleOperation},CancellationToken)" />.
    /// </summary>
    /// <param name="user">The tuple's user/subject (strongly typed)</param>
    /// <param name="relation">The relation (bound to the object type)</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="ct">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <exception cref="FgaWriteException">Thrown when the write fails.</exception>
    Task WriteAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        CancellationToken ct = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Deletes one strongly-typed tuple from OpenFGA. Idempotent server-side: deleting a
    ///     never-stored tuple is a no-op (<c>OnMissingDeletes = Ignore</c>), not an error. Thin
    ///     wrapper over
    ///     <see cref="DeleteAsync(IReadOnlyCollection{SealedFgaTupleOperation},CancellationToken)" />.
    /// </summary>
    /// <param name="user">The tuple's user/subject (strongly typed)</param>
    /// <param name="relation">The relation (bound to the object type)</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="ct">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <exception cref="FgaWriteException">Thrown when the delete fails.</exception>
    Task DeleteAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        CancellationToken ct = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>;

    /// <summary>
    ///     Writes a batch of strongly-typed tuples to OpenFGA in one request. Build the operations via
    ///     <see cref="SealedFgaTupleOperation.Of{TObjId}" />. Idempotent (<c>OnDuplicateWrites = Ignore</c>);
    ///     the same shared write currency as the outbox enqueue API.
    ///     <para>
    ///         Applies to OpenFGA <b>immediately and synchronously</b> — it does not enqueue and is not
    ///         affected by <see cref="SealedFgaOptions.RunOutboxDrainer" />. For a tuple change that must
    ///         commit atomically with a database transaction, use the <c>DbContext.EnqueueFga*</c>
    ///         extensions (or a <see cref="Attributes.SealedFgaRelationAttribute" /> on the FK) instead.
    ///     </para>
    /// </summary>
    /// <param name="writes">The tuples to write.</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="FgaWriteException">Thrown when any tuple fails to write.</exception>
    Task WriteAsync(IReadOnlyCollection<SealedFgaTupleOperation> writes, CancellationToken ct = new());

    /// <summary>
    ///     Deletes a batch of strongly-typed tuples from OpenFGA in one request. Build the operations
    ///     via <see cref="SealedFgaTupleOperation.Of{TObjId}" />. Idempotent (<c>OnMissingDeletes = Ignore</c>).
    ///     <para>
    ///         Applies to OpenFGA <b>immediately and synchronously</b> — it does not enqueue and is not
    ///         affected by <see cref="SealedFgaOptions.RunOutboxDrainer" />. For a tuple change that must
    ///         commit atomically with a database transaction, use the <c>DbContext.EnqueueFga*</c>
    ///         extensions (or a <see cref="Attributes.SealedFgaRelationAttribute" /> on the FK) instead.
    ///     </para>
    /// </summary>
    /// <param name="deletes">The tuples to delete.</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="FgaWriteException">Thrown when any tuple fails to delete.</exception>
    Task DeleteAsync(IReadOnlyCollection<SealedFgaTupleOperation> deletes, CancellationToken ct = new());

    /// <summary>
    ///     Applies writes and deletes as a single OpenFGA write request — the immediate-write mirror of
    ///     the outbox drainer's combined apply. Deletes and writes are sent together; ignore semantics
    ///     make the operation idempotent. Build the operations via
    ///     <see cref="SealedFgaTupleOperation.Of{TObjId}" />.
    /// </summary>
    /// <param name="writes">The tuples that must exist.</param>
    /// <param name="deletes">The tuples that must not exist.</param>
    /// <param name="ct">Cancellation token</param>
    /// <exception cref="FgaWriteException">Thrown when any tuple fails to apply.</exception>
    Task ApplyAsync(
        IReadOnlyCollection<SealedFgaTupleOperation> writes,
        IReadOnlyCollection<SealedFgaTupleOperation> deletes,
        CancellationToken ct = new()
    );
}
