using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Util;

namespace SealedFga.ModelBinder;

/// <summary>
///     Model binder for binding FGA entity query parameters annotated with <see cref="FgaAuthorizeListAttribute" />.
/// </summary>
public class SealedFgaEntityListModelBinder(Type dbContextType)
    : SealedFgaModelBinder<FgaAuthorizeListAttribute>(dbContextType) {
    /// <summary>
    ///     Used to bind an FGA entity query parameter that has been annotated with the
    ///     <see cref="FgaAuthorizeListAttribute" />. Retrieves the IDs of all objects of the given type
    ///     for which the user has the required <c>Relation</c> and injects an <b>unmaterialized</b>
    ///     <see cref="IQueryable{T}" /> filtered to exactly those IDs. The action composes paging /
    ///     sorting / projections onto it and materializes the query itself — everything still
    ///     translates to SQL, so e.g. pagination happens in the database.
    /// </summary>
    /// <example>
    ///     <code>
    ///     public async Task&lt;IActionResult&gt; GetSecrets(
    ///         [FgaAuthorizeList(Relation = nameof(SecretEntityIdPermissions.can_view))]
    ///         IQueryable&lt;SecretEntity&gt; secrets
    ///     ) => Ok(await secrets.ToListAsync());
    /// </code>
    /// </example>
    /// <param name="context">The model binding context.</param>
    /// <param name="param">The annotated parameter.</param>
    /// <param name="sealedFgaService">The SealedFGA service.</param>
    /// <param name="openFgaRawUserString">
    ///     The raw OpenFGA user string from the HttpContext's User ClaimsPrincipal.
    /// </param>
    /// <param name="attr">The parameter's annotation.</param>
    protected override async Task FgaBind(
        ModelBindingContext context,
        ControllerParameterDescriptor param,
        SealedFgaService sealedFgaService,
        string openFgaRawUserString,
        FgaAuthorizeListAttribute attr
    ) {
        if (!context.ModelType.IsGenericType
            || context.ModelType.GetGenericTypeDefinition() != typeof(IQueryable<>)) {
            throw new InvalidOperationException(
                $"[FgaAuthorizeList] on parameter '{param.Name}' of action "
                + $"'{context.ActionContext.ActionDescriptor.DisplayName}' must be declared as "
                + $"IQueryable<TEntity>, but is '{context.ModelType.Name}'. The binder injects a composable, "
                + "authorization-filtered EF query; materialize it in the action (e.g. via ToListAsync), "
                + "optionally after composing paging."
            );
        }

        var dbContext = GetDbContext(context);
        var entityType = context.ModelType.GetGenericArguments()[0]; // e.g. IQueryable<SecretEntity> -> SecretEntity

        // Get the ID type from ISealedFgaType<TId> interface
        var sealedFgaTypeInterface = entityType.GetInterfaces()
                                               .FirstOrDefault(i => i.IsGenericType &&
                                                                    i.GetGenericTypeDefinition() ==
                                                                    typeof(ISealedFgaType<>)
                                                );

        if (sealedFgaTypeInterface == null) {
            throw new InvalidOperationException(
                $"Entity type '{entityType.Name}' bound with [FgaAuthorizeList] on action "
                + $"'{context.ActionContext.ActionDescriptor.DisplayName}' does not implement ISealedFgaType<TId>."
            );
        }

        var idType = sealedFgaTypeInterface.GetGenericArguments()[0]; // e.g. SecretEntityId

        // Use reflection to get the OpenFGA type name from the static property
        var openFgaTypeNameProperty = SealedFgaBinderReflectionCache.OpenFgaTypeNameProperty(idType);
        var openFgaTypeName = (string?) openFgaTypeNameProperty.GetValue(null)
                              ?? throw new InvalidOperationException(
                                  $"Static property '{GeneratedNames.OpenFgaTypeNamePropertyName}' on ID type "
                                  + $"'{idType.Name}' returned null."
                              );

        // Resolve the access verdict from the optional provider: FullAccess (skip ListObjects and
        // hand over the unfiltered DbSet), ScopedToIds (a caller-computed ID set), or Normal
        // (ListObjects with the returned options). No provider registered => Normal with no options.
        var verdict = await GetBinderListVerdictAsync(
            context,
            openFgaRawUserString,
            attr.Relation,
            openFgaTypeName
        );

        // Get the DbSet (via Set<T>() like the entity binder — no declared DbSet property required).
        var dbSet = SealedFgaBinderReflectionCache.Set(entityType).Invoke(dbContext, null)
                    ?? throw new InvalidOperationException(
                        $"DbContext.Set<{entityType.Name}>() on '{dbContext.GetType().Name}' returned null."
                    );

        object filteredQuery;
        if (verdict.Kind == SealedFgaListVerdict.VerdictKind.FullAccess) {
            // Full access: no authorization filtering. EF global query filters still apply because
            // the query is still the DbSet. The action composes paging/sorting/projections as usual.
            filteredQuery = dbSet;
        } else {
            // Collect the authorized raw IDs — either the provider's custom scope, or ListObjects.
            IEnumerable<string> rawObjectIds;
            if (verdict.Kind == SealedFgaListVerdict.VerdictKind.ScopedToIds) {
                rawObjectIds = verdict.ObjectIds!;
            } else {
                var authorizedObjectStrings = await sealedFgaService.ListObjectsAsync(
                    openFgaRawUserString,
                    attr.Relation,
                    openFgaTypeName,
                    verdict.Options,
                    context.HttpContext.RequestAborted
                );

                // OpenFGA returns full "type:id" object strings; strip the type prefix.
                rawObjectIds = authorizedObjectStrings.Select(StripTypePrefix);
            }

            // Parse authorized IDs into strong ID types. An empty ID list stays a regular EF query
            // (translated to a false predicate) so the action can compose/materialize it the same way.
            var listType = typeof(List<>).MakeGenericType(idType);
            var authorizedIds = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod(nameof(List<>.Add))!;
            foreach (var rawId in rawObjectIds) {
                addMethod.Invoke(authorizedIds, [IdUtil.ParseId(idType, rawId)]);
            }

            // Use LINQ to filter entities by authorized IDs: entity => authorizedIds.Contains(entity.Id)
            var whereMethod = SealedFgaBinderReflectionCache.Where(entityType);
            var parameter = Expression.Parameter(entityType, "entity");
            var idProperty = Expression.Property(parameter, nameof(ISealedFgaType<>.Id));
            var containsMethod = SealedFgaBinderReflectionCache.Contains(idType);
            var containsCall =
                Expression.Call(containsMethod, Expression.Constant(authorizedIds, listType), idProperty);
            var lambda = Expression.Lambda(containsCall, parameter);

            filteredQuery = whereMethod.Invoke(null, [dbSet, lambda])!;
        }

        // Eager-load any requested navigation properties.
        filteredQuery = ApplyIncludes(filteredQuery, entityType, attr.Include);

        // Hand the action the unmaterialized query. MVC's validation must not touch it: visiting an
        // IQueryable model would enumerate it, silently executing the query before the action runs.
        context.ValidationState[filteredQuery] = new ValidationStateEntry { SuppressValidation = true };
        context.Result = ModelBindingResult.Success(filteredQuery);
    }

    /// <summary>Strips the <c>type:</c> prefix from an OpenFGA object string, returning the raw ID.</summary>
    private static string StripTypePrefix(string objectString) {
        var separatorIndex = objectString.IndexOf(':');
        return separatorIndex >= 0 ? objectString.Substring(separatorIndex + 1) : objectString;
    }
}
