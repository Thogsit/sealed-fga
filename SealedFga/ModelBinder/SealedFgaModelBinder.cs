using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SealedFga.Fga;

namespace SealedFga.ModelBinder;

/// <summary>
///     Abstract base class for FGA model binders.
/// </summary>
/// <typeparam name="TAttr">The attribute's type to annotate model binding.</typeparam>
/// <param name="dbContextType">The type of the database context.</param>
public abstract class SealedFgaModelBinder<TAttr>(Type dbContextType) : IModelBinder where TAttr : Attribute {
    /// <summary>
    ///     The open generic <c>EntityFrameworkQueryableExtensions.Include&lt;TEntity&gt;(IQueryable&lt;TEntity&gt;, string)</c>
    ///     method, resolved once. The string overload is used so callers can pass navigation property paths.
    /// </summary>
    private static readonly MethodInfo StringIncludeMethod =
        typeof(EntityFrameworkQueryableExtensions)
           .GetMethods()
           .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.Include)
                       && m.IsGenericMethodDefinition
                       && m.GetGenericArguments().Length == 1
                       && m.GetParameters().Length == 2
                       && m.GetParameters()[1].ParameterType == typeof(string)
            );

    /// <inheritdoc />
    public async Task BindModelAsync(ModelBindingContext context) {
        // Retrieve fga entity parameter, e.g. SecretEntity
        var param = context.ActionContext.ActionDescriptor
                           .Parameters
                           .OfType<ControllerParameterDescriptor>()
                           .FirstOrDefault(p => p.Name == context.FieldName);
        var attr = (TAttr?) param?
                           .ParameterInfo
                           .GetCustomAttributes(typeof(TAttr), false)
                           .FirstOrDefault();
        var sealedFgaService = context.HttpContext.RequestServices.GetRequiredService<SealedFgaService>();
        var userClaimType = context.HttpContext.RequestServices
                                   .GetRequiredService<IOptions<SealedFgaOptions>>().Value.UserClaimType;
        var rawUserString = context.HttpContext.User.Claims.FirstOrDefault(c => c.Type == userClaimType)?.Value;
        if (param is null || attr is null || rawUserString is null) {
            return;
        }

        await FgaBind(context, param, sealedFgaService, rawUserString, attr);
    }

    /// <summary>
    ///     Gets the database context from the model binding context.
    /// </summary>
    /// <param name="context">The model binding context.</param>
    /// <returns>The database context.</returns>
    protected DbContext GetDbContext(ModelBindingContext context)
        => (DbContext) context.HttpContext.RequestServices.GetRequiredService(dbContextType);

    /// <summary>
    ///     Applies EF <c>Include</c> navigation-property paths to a query. Returns the query unchanged when no
    ///     includes are requested.
    /// </summary>
    /// <param name="query">The <see cref="IQueryable{T}" /> to compose includes onto.</param>
    /// <param name="entityType">The queried entity's CLR type.</param>
    /// <param name="includes">The navigation property paths to eager-load, or <c>null</c>/empty for none.</param>
    /// <returns>The query with the requested includes applied.</returns>
    protected static object ApplyIncludes(object query, Type entityType, string[]? includes) {
        if (includes is null || includes.Length == 0) {
            return query;
        }

        var includeMethod = StringIncludeMethod.MakeGenericMethod(entityType);
        foreach (var path in includes) {
            query = includeMethod.Invoke(null, [query, path])!;
        }

        return query;
    }

    /// <summary>
    ///     Performs the FGA-specific binding logic.
    /// </summary>
    /// <param name="context">The model binding context.</param>
    /// <param name="param">The controller parameter descriptor.</param>
    /// <param name="sealedFgaService">The SealedFGA service.</param>
    /// <param name="openFgaRawUserString">
    ///     The raw OpenFGA user string from the HttpContext's User ClaimsPrincipal.
    /// </param>
    /// <param name="attr">The attribute instance.</param>
    protected abstract Task FgaBind(
        ModelBindingContext context,
        ControllerParameterDescriptor param,
        SealedFgaService sealedFgaService,
        string openFgaRawUserString,
        TAttr attr
    );
}
