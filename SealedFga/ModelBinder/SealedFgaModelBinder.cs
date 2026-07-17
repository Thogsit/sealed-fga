using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SealedFga.Exceptions;
using SealedFga.Fga;

namespace SealedFga.ModelBinder;

/// <summary>
///     Abstract base class for FGA model binders. Every misconfiguration fails loudly: a silent
///     no-bind would hand the action a <c>null</c> argument and surface as an NRE far from the
///     actual cause — unacceptable for an authorization component.
/// </summary>
/// <typeparam name="TAttr">The attribute's type to annotate model binding.</typeparam>
/// <param name="dbContextType">The type of the database context.</param>
public abstract class SealedFgaModelBinder<TAttr>(Type dbContextType) : IModelBinder where TAttr : Attribute {
    /// <inheritdoc />
    public async Task BindModelAsync(ModelBindingContext context) {
        // Retrieve fga entity parameter, e.g. SecretEntity
        var param = context.ActionContext.ActionDescriptor
                           .Parameters
                           .OfType<ControllerParameterDescriptor>()
                           .FirstOrDefault(p => p.Name == context.FieldName);
        if (param is null) {
            throw new InvalidOperationException(
                $"SealedFGA model binding failed for field '{context.FieldName}' on action "
                + $"'{context.ActionContext.ActionDescriptor.DisplayName}': no matching controller action parameter was found."
            );
        }

        var attr = (TAttr?) param.ParameterInfo
                                 .GetCustomAttributes(typeof(TAttr), false)
                                 .FirstOrDefault();
        if (attr is null) {
            throw new InvalidOperationException(
                $"SealedFGA model binding failed for parameter '{param.Name}' on action "
                + $"'{context.ActionContext.ActionDescriptor.DisplayName}': the parameter uses a SealedFGA "
                + $"model binder but is not annotated with [{typeof(TAttr).Name}]."
            );
        }

        var sealedFgaService = context.HttpContext.RequestServices.GetRequiredService<SealedFgaService>();
        var userClaimType = context.HttpContext.RequestServices
                                   .GetRequiredService<IOptions<SealedFgaOptions>>().Value.UserClaimType;
        var rawUserString = context.HttpContext.User.Claims.FirstOrDefault(c => c.Type == userClaimType)?.Value;
        if (rawUserString is null) {
            throw new FgaUnauthenticatedException(userClaimType);
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
    ///     Resolves the optional <see cref="ISealedFgaBinderOptionsProvider" /> hook and asks it for
    ///     per-call options (contextual tuples, consistency) for the operation about to run.
    ///     Returns <c>null</c> — default behavior — when no provider is registered.
    /// </summary>
    /// <param name="context">The model binding context.</param>
    /// <param name="rawUser">The user's OpenFGA tuple string.</param>
    /// <param name="relation">The relation being checked/listed.</param>
    /// <param name="objectTypeName">The OpenFGA type name of the object side.</param>
    /// <param name="operation">Whether this is a single check or a list operation.</param>
    protected static async ValueTask<SealedFgaQueryOptions?> GetBinderQueryOptionsAsync(
        ModelBindingContext context,
        string rawUser,
        string relation,
        string objectTypeName,
        SealedFgaBinderOperation operation
    ) {
        var provider = context.HttpContext.RequestServices.GetService<ISealedFgaBinderOptionsProvider>();
        if (provider is null) {
            return null;
        }

        return await provider.GetOptionsAsync(new SealedFgaBinderOptionsContext(
            context.HttpContext,
            rawUser,
            relation,
            objectTypeName,
            operation
        ));
    }

    /// <summary>
    ///     Resolves the optional <see cref="ISealedFgaBinderOptionsProvider" /> hook for a list binding
    ///     and asks it for the access <see cref="SealedFgaListVerdict" />. Returns
    ///     <see cref="SealedFgaListVerdict.Normal(SealedFgaQueryOptions?)" /> with no options — default
    ///     scoping — when no provider is registered.
    /// </summary>
    /// <param name="context">The model binding context.</param>
    /// <param name="rawUser">The user's OpenFGA tuple string.</param>
    /// <param name="relation">The relation being listed.</param>
    /// <param name="objectTypeName">The OpenFGA type name of the object side.</param>
    protected static async ValueTask<SealedFgaListVerdict> GetBinderListVerdictAsync(
        ModelBindingContext context,
        string rawUser,
        string relation,
        string objectTypeName
    ) {
        var provider = context.HttpContext.RequestServices.GetService<ISealedFgaBinderOptionsProvider>();
        if (provider is null) {
            return SealedFgaListVerdict.Normal();
        }

        return await provider.GetListVerdictAsync(new SealedFgaBinderOptionsContext(
            context.HttpContext,
            rawUser,
            relation,
            objectTypeName,
            SealedFgaBinderOperation.List
        ));
    }

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

        var includeMethod = SealedFgaBinderReflectionCache.Include(entityType);
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
