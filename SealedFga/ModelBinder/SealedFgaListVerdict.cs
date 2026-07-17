using System;
using System.Collections.Generic;
using SealedFga.Fga;

namespace SealedFga.ModelBinder;

/// <summary>
///     The access decision an <see cref="ISealedFgaBinderOptionsProvider" /> returns for a
///     <c>[FgaAuthorizeList]</c> binding. It exists because a per-object contextual tuple cannot
///     express "granted on <b>every</b> object" (OpenFGA has no object wildcard on the object side),
///     so a super-user whose access comes only from a request-time tuple would otherwise be silently
///     scoped like a normal user on list endpoints. The verdict lets the provider escape the
///     <c>ListObjects</c> path entirely.
/// </summary>
public sealed class SealedFgaListVerdict {
    /// <summary>Which branch the list binder takes.</summary>
    public enum VerdictKind {
        /// <summary>Run <c>ListObjects</c> with <see cref="Options" /> (the default behavior).</summary>
        Normal,

        /// <summary>Skip <c>ListObjects</c>; hand the action the unfiltered <c>DbSet&lt;TEntity&gt;</c>.</summary>
        FullAccess,

        /// <summary>Skip <c>ListObjects</c>; scope the query to <see cref="ObjectIds" />.</summary>
        ScopedToIds,
    }

    private SealedFgaListVerdict(
        VerdictKind kind,
        SealedFgaQueryOptions? options,
        IReadOnlyCollection<string>? objectIds
    ) {
        Kind = kind;
        Options = options;
        ObjectIds = objectIds;
    }

    /// <summary>The branch this verdict selects.</summary>
    public VerdictKind Kind { get; }

    /// <summary>The per-call options used on the <c>ListObjects</c> query; set only for <see cref="VerdictKind.Normal" />.</summary>
    public SealedFgaQueryOptions? Options { get; }

    /// <summary>
    ///     The raw object IDs (the <c>id</c> part, without the <c>type:</c> prefix) to scope the query
    ///     to; set only for <see cref="VerdictKind.ScopedToIds" />.
    /// </summary>
    public IReadOnlyCollection<string>? ObjectIds { get; }

    /// <summary>
    ///     Normal scoping: the binder runs <c>ListObjects</c> with the given options (contextual
    ///     tuples, consistency), exactly as when no provider is registered.
    /// </summary>
    public static SealedFgaListVerdict Normal(SealedFgaQueryOptions? options = null)
        => new(VerdictKind.Normal, options, null);

    /// <summary>
    ///     Full access: skip authorization filtering entirely and hand the action the unfiltered
    ///     <c>DbSet&lt;TEntity&gt;</c> as a composable <c>IQueryable</c> (EF global query filters still
    ///     apply). The fit for a super-user whose access can't be expressed as per-object tuples.
    /// </summary>
    public static readonly SealedFgaListVerdict FullAccess = new(VerdictKind.FullAccess, null, null);

    /// <summary>
    ///     Custom scoping: skip <c>ListObjects</c> and filter the query to exactly these object IDs
    ///     (the caller computed them itself, e.g. by batch-checking every candidate with its own
    ///     per-object contextual tuple). Each entry is the raw <c>id</c>, without the <c>type:</c> prefix.
    /// </summary>
    public static SealedFgaListVerdict ScopedToIds(IReadOnlyCollection<string> objectIds) {
        ArgumentNullException.ThrowIfNull(objectIds);
        return new SealedFgaListVerdict(VerdictKind.ScopedToIds, null, objectIds);
    }
}
