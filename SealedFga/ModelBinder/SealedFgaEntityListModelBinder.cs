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

        // Resolve the optional per-call options hook (contextual tuples, consistency).
        var queryOptions = await GetBinderQueryOptionsAsync(
            context,
            openFgaRawUserString,
            attr.Relation,
            openFgaTypeName,
            SealedFgaBinderOperation.List
        );

        // Get authorized object IDs using ListObjectsAsync with raw parameters
        var authorizedObjectStrings = await sealedFgaService.ListObjectsAsync(
            openFgaRawUserString,
            attr.Relation,
            openFgaTypeName,
            queryOptions,
            context.HttpContext.RequestAborted
        );

        // Parse authorized IDs into strong ID types. OpenFGA returns full "type:id" object strings;
        // strip the type prefix before parsing the raw ID.
        var listType = typeof(List<>).MakeGenericType(idType);
        var authorizedIds = Activator.CreateInstance(listType);
        var addMethod = listType.GetMethod(nameof(List<>.Add))!;
        foreach (var aos in authorizedObjectStrings) {
            var separatorIndex = aos.IndexOf(':');
            var rawId = separatorIndex >= 0 ? aos.Substring(separatorIndex + 1) : aos;
            addMethod.Invoke(authorizedIds, [IdUtil.ParseId(idType, rawId)]);
        }

        // Get the DbSet (via Set<T>() like the entity binder — no declared DbSet property required)
        // and filter by authorized IDs. An empty ID list stays a regular EF query (translated to a
        // false predicate) so the action can compose/materialize it the same way.
        var dbSet = SealedFgaBinderReflectionCache.Set(entityType).Invoke(dbContext, null)
                    ?? throw new InvalidOperationException(
                        $"DbContext.Set<{entityType.Name}>() on '{dbContext.GetType().Name}' returned null."
                    );

        // Use LINQ to filter entities by authorized IDs
        var whereMethod = SealedFgaBinderReflectionCache.Where(entityType);

        // Create lambda: entity => authorizedIds.Contains(entity.Id)
        var parameter = Expression.Parameter(entityType, "entity");
        var idProperty = Expression.Property(parameter, nameof(ISealedFgaType<>.Id));
        var containsMethod = SealedFgaBinderReflectionCache.Contains(idType);
        var containsCall = Expression.Call(containsMethod, Expression.Constant(authorizedIds, listType), idProperty);
        var lambda = Expression.Lambda(containsCall, parameter);

        var filteredQuery = whereMethod.Invoke(null, [dbSet, lambda])!;

        // Eager-load any requested navigation properties.
        filteredQuery = ApplyIncludes(filteredQuery, entityType, attr.Include);

        // Hand the action the unmaterialized query. MVC's validation must not touch it: visiting an
        // IQueryable model would enumerate it, silently executing the query before the action runs.
        context.ValidationState[filteredQuery] = new ValidationStateEntry { SuppressValidation = true };
        context.Result = ModelBindingResult.Success(filteredQuery);
    }
}
