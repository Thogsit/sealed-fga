using System;
using Microsoft.AspNetCore.Mvc;
using SealedFga.ModelBinder;

namespace SealedFga.Attributes;

/// <summary>
///     Specifies that the parameter should be bound as an authorization-filtered
///     <see cref="System.Linq.IQueryable{T}" /> containing exactly the entities the current user has
///     the given <see cref="Relation" /> to. The query is injected unmaterialized, so the action can
///     compose paging/sorting onto it before materializing (everything still translates to SQL).
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FgaAuthorizeListAttribute() : ModelBinderAttribute(typeof(SealedFgaEntityListModelBinder)) {
    /// <summary>
    ///     The relation to check for authorization.
    /// </summary>
    public required string Relation { get; set; }

    /// <summary>
    ///     Optional navigation properties to eager-load (EF <c>Include</c>) on each bound entity. Prefer the
    ///     generated <c>{Entity}Includes</c> constants, e.g.
    ///     <c>Include = [nameof(SecretEntityIncludes.OwningAgency)]</c>.
    /// </summary>
    public string[]? Include { get; set; }
}
