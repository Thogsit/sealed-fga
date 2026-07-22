using System.Threading;
using System.Threading.Tasks;
using SealedFga.AuthModel;

namespace SealedFga.Fga;

/// <summary>
///     Optional extensibility hook for the direct <see cref="ISealedFgaService" /> path: when an
///     implementation is registered in DI, <see cref="SealedFgaService" /> resolves it on every
///     operation that targets a <b>concrete object</b> and merges the returned per-call options
///     (contextual tuples, consistency) with any explicit per-call <see cref="SealedFgaQueryOptions" />.
///     Contextual tuples are unioned; an explicit per-call consistency wins over the ambient one.
///     <para>
///         This mirrors, for direct service calls, what <c>ISealedFgaBinderOptionsProvider</c> does for
///         the model binders. Typical use: injecting a request-scoped super-user contextual tuple derived
///         from the current principal, without threading a <c>queryOptions</c> argument through every
///         call site.
///     </para>
///     <para>
///         No registration keeps the service's default behavior with zero overhead (the provider is
///         never consulted). The library stays claims-agnostic: it passes only <c>(user, relation,
///         object)</c> and the implementation decides relevance.
///     </para>
///     <para>
///         <b>Not consulted for <see cref="ISealedFgaService.ListObjectsAsync{TObjId}" /> or
///         <see cref="ISealedFgaService.ListUsersAsync{TObjId,TUserId}" /></b>: <c>ListObjects</c> has no
///         concrete object to key a per-object contextual tuple on (the "super-user sees everything" list
///         path needs the consumer's DB, exactly as the binder path solves it with
///         <c>SealedFgaListVerdict.FullAccess</c>), and a super-user contextual tuple does not change the
///         subjects <c>ListUsers</c> enumerates.
///     </para>
/// </summary>
public interface ISealedFgaAmbientOptionsProvider {
    /// <summary>
    ///     Returns the per-call options (contextual tuples / consistency) to apply to one check-shaped
    ///     operation against a concrete object, or <c>null</c> for none. Called once per object — for a
    ///     batch check it is invoked per item with that item's object.
    /// </summary>
    /// <param name="user">The tuple's user/subject being checked.</param>
    /// <param name="relation">The relation being checked (bound to the object type).</param>
    /// <param name="objectId">The concrete object the operation targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <typeparam name="TObjId">The object ID type.</typeparam>
    ValueTask<SealedFgaQueryOptions?> GetCheckOptionsAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        CancellationToken cancellationToken = default
    ) where TObjId : ISealedFgaTypeId<TObjId>;
}
