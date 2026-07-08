using System;
using Microsoft.AspNetCore.Mvc;
using SealedFga.ModelBinder;

namespace SealedFga.Attributes;

/// <summary>
///     Specifies that the parameter should be bound as a list using FGA authorization for a specific relation.
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
