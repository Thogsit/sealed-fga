using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using SealedFga.Fga;

namespace SealedFga.ModelBinder;

/// <summary>
///     Optional extensibility hook for the SealedFGA model binders: when an implementation is
///     registered in DI, every binder-driven check/list resolves it and applies the returned
///     per-call options (contextual tuples, consistency) to the underlying OpenFGA request.
///     Typical use: injecting a super-user contextual tuple derived from the current request's
///     claims, or forcing <c>HIGHERCONSISTENCY</c> on list operations.
///     <para>
///         No registration (or returning <c>null</c>) keeps the binders' default behavior.
///     </para>
/// </summary>
public interface ISealedFgaBinderOptionsProvider {
    /// <summary>
    ///     Returns the per-call options for one binder-driven OpenFGA operation, or <c>null</c> for none.
    /// </summary>
    /// <param name="context">The operation about to be performed.</param>
    ValueTask<SealedFgaQueryOptions?> GetOptionsAsync(SealedFgaBinderOptionsContext context);

    /// <summary>
    ///     Returns the access <see cref="SealedFgaListVerdict" /> for a <c>[FgaAuthorizeList]</c>
    ///     binding — the hook that lets a provider grant full access (skip <c>ListObjects</c> and hand
    ///     the action the unfiltered <c>DbSet</c>) or a custom ID scope, instead of only supplying
    ///     options. Only invoked for <see cref="SealedFgaBinderOperation.List" />.
    ///     <para>
    ///         The default keeps the pre-verdict behavior: it wraps <see cref="GetOptionsAsync" /> in
    ///         a <see cref="SealedFgaListVerdict.Normal(SealedFgaQueryOptions?)" /> verdict, so existing
    ///         providers work unchanged. Override it to return
    ///         <see cref="SealedFgaListVerdict.FullAccess" /> or
    ///         <see cref="SealedFgaListVerdict.ScopedToIds" /> for super-user / custom scenarios.
    ///     </para>
    /// </summary>
    /// <param name="context">The list operation about to be performed.</param>
    async ValueTask<SealedFgaListVerdict> GetListVerdictAsync(SealedFgaBinderOptionsContext context)
        => SealedFgaListVerdict.Normal(await GetOptionsAsync(context));
}

/// <summary>The kind of OpenFGA operation a SealedFGA model binder is about to perform.</summary>
public enum SealedFgaBinderOperation {
    /// <summary>A single permission check (<c>[FgaAuthorize]</c> entity binding).</summary>
    Check,

    /// <summary>An object listing (<c>[FgaAuthorizeList]</c> query binding).</summary>
    List,
}

/// <summary>
///     Describes the binder-driven OpenFGA operation an <see cref="ISealedFgaBinderOptionsProvider" />
///     is asked to supply options for.
/// </summary>
/// <param name="HttpContext">The current request.</param>
/// <param name="RawUser">The user's OpenFGA tuple string (from the configured user claim), e.g. <c>user:alice</c>.</param>
/// <param name="Relation">The relation being checked/listed, as declared on the binder attribute.</param>
/// <param name="ObjectTypeName">The OpenFGA type name of the object side, e.g. <c>secret</c>.</param>
/// <param name="Operation">Whether this is a single check or a list operation.</param>
public sealed record SealedFgaBinderOptionsContext(
    HttpContext HttpContext,
    string RawUser,
    string Relation,
    string ObjectTypeName,
    SealedFgaBinderOperation Operation
);
