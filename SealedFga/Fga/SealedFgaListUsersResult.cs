using System.Collections.Generic;
using SealedFga.AuthModel;

namespace SealedFga.Fga;

/// <summary>
///     The typed result of <see cref="SealedFgaService.ListUsersAsync{TObjId,TUserId}" />. OpenFGA's
///     <c>ListUsers</c> can return three shapes of subject for the requested user type, and this
///     result surfaces each one <b>explicitly</b> rather than collapsing them — silently dropping a
///     wildcard would hide a "granted to every user of this type" fact, which is an authorization
///     signal callers must handle.
/// </summary>
/// <typeparam name="TUserId">The strongly-typed user ID type that was filtered for.</typeparam>
public sealed class SealedFgaListUsersResult<TUserId> where TUserId : ISealedFgaTypeId<TUserId> {
    /// <param name="users">Concrete subjects (<c>type:id</c>) of the requested type.</param>
    /// <param name="usersets">Userset subjects (<c>type:id#relation</c>) of the requested type.</param>
    /// <param name="hasWildcard">Whether a public/typed wildcard (<c>type:*</c>) was returned.</param>
    public SealedFgaListUsersResult(
        IReadOnlyList<TUserId> users,
        IReadOnlyList<SealedFgaListUsersUserset<TUserId>> usersets,
        bool hasWildcard
    ) {
        Users = users;
        Usersets = usersets;
        HasWildcard = hasWildcard;
    }

    /// <summary>Concrete subjects (<c>type:id</c>) of the requested type that hold the relation.</summary>
    public IReadOnlyList<TUserId> Users { get; }

    /// <summary>
    ///     Userset subjects (<c>type:id#relation</c>) of the requested type. Normally empty unless the
    ///     model resolves the relation to usersets of this type.
    /// </summary>
    public IReadOnlyList<SealedFgaListUsersUserset<TUserId>> Usersets { get; }

    /// <summary>
    ///     Whether OpenFGA returned a typed wildcard (<c>type:*</c>) — i.e. <b>every</b> user of the
    ///     requested type holds the relation. Callers must handle this rather than treating
    ///     <see cref="Users" /> as the complete set.
    /// </summary>
    public bool HasWildcard { get; }
}

/// <summary>
///     A userset subject returned by <see cref="SealedFgaService.ListUsersAsync{TObjId,TUserId}" />,
///     i.e. the OpenFGA subject <c>type:id#relation</c>.
/// </summary>
/// <typeparam name="TUserId">The strongly-typed user ID type.</typeparam>
/// <param name="Id">The userset's object ID.</param>
/// <param name="Relation">The userset's relation (raw OpenFGA relation string).</param>
public sealed record SealedFgaListUsersUserset<TUserId>(TUserId Id, string Relation)
    where TUserId : ISealedFgaTypeId<TUserId>;
