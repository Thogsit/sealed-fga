using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Model;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Exceptions;
using SealedFga.Fga;
using SealedFga.Generators.AuthModel;
using SealedFga.Util;

namespace SealedFga.ModelBinder;

/// <summary>
///     Model binder for binding FGA entity parameters annotated with <see cref="FgaAuthorizeAttribute" />.
/// </summary>
public class SealedFgaEntityModelBinder(Type dbContextType)
    : SealedFgaModelBinder<FgaAuthorizeAttribute>(dbContextType) {
    /// <summary>
    ///     Used to bind FGA entity parameters that have been annotated with the <see cref="FgaAuthorizeAttribute" />.
    ///     Checks whether the user has the required permission given by <c>Relation</c>.
    ///     If valid, retrieves the entity from the DB and injects it into the annotated parameter.
    /// </summary>
    /// <example>
    ///     <code>
    ///     public async Task&lt;IActionResult&gt; GetSecret(
    ///         SecretEntityId secretId,
    ///         [FgaAuthorize(
    ///             Relation = nameof(SecretEntityIdAttributes.can_view),
    ///             ParameterName = nameof(secretId))
    ///         ] SecretEntity secret
    ///     );
    ///     </code>
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
        FgaAuthorizeAttribute attr
    ) {
        // Get the value of the parameter specified in ParameterName, e.g. "15ff5687-3f4d-4cae-8a19-68670e5cdff2"
        var parameterVal = (string?) context.ActionContext.RouteData.Values[attr.ParameterName];
        if (parameterVal == null) {
            return;
        }

        // Get the ID type of the ID parameter, e.g. "SecretEntityId"
        var idType = context.ActionContext.ActionDescriptor
                            .Parameters
                            .OfType<ControllerParameterDescriptor>()
                            .FirstOrDefault(p => p.Name == attr.ParameterName);
        if (idType == null) {
            return;
        }

        // Convert "raw" string ID into strongly typed ID, e.g. "15ff5687-3f4d-4cae-8a19-68670e5cdff2" -> SecretEntityId(...)
        var parseMethod = idType.ParameterInfo.ParameterType.GetMethod(TypeNameIdGenerator.ParseMethodName);
        if (parseMethod == null) {
            return;
        }

        var idVal = parseMethod.Invoke(null, [parameterVal]);
        if (idVal == null) {
            return;
        }

        var parsedId = IdUtil.ParseId(idType.ParameterInfo.ParameterType, parameterVal);

        // Retrieve the OpenFGA ID tuple string from the ID object, e.g. SecretEntityId(...) -> "secret:15ff5687-3f4d-4cae-8a19-68670e5cdff2"
        var openFgaIdTupleStringMethod =
            idType.ParameterInfo.ParameterType.GetMethod(TypeNameIdGenerator.OpenFgaIdTupleStringMethodName);
        var openFgaRawObjectString = (string?) openFgaIdTupleStringMethod?.Invoke(parsedId, null);
        if (openFgaRawObjectString == null) {
            return;
        }

        // Check if the user has the required permission
        var isAuthorized = await sealedFgaService.CheckAsync(
            new TupleKey {
                User = openFgaRawUserString,
                Relation = attr.Relation,
                Object = openFgaRawObjectString,
            }
        );

        if (!isAuthorized) {
            throw new FgaForbiddenException(openFgaRawUserString, attr.Relation, openFgaRawObjectString);
        }

        // Load the entity from the DbSet, eager-loading any requested navigations.
        var dbContext = GetDbContext(context);
        var entityType = context.ModelType;
        var setMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)
                                        ?.MakeGenericMethod(entityType);
        if (setMethod == null) {
            return;
        }

        var dbSet = setMethod.Invoke(dbContext, null);
        if (dbSet == null) {
            return;
        }

        // With includes we must go through the LINQ query pipeline (FindAsync cannot eager-load); without
        // them, keep the cheaper FindAsync primary-key lookup that also serves cached/tracked entities.
        var entity = attr.Include is { Length: > 0 }
            ? await LoadWithIncludesAsync(
                dbSet,
                entityType,
                idType.ParameterInfo.ParameterType,
                parsedId,
                attr.Include,
                context.HttpContext.RequestAborted
            )
            : await FindByPrimaryKeyAsync(dbSet, parsedId);

        if (entity != null) {
            context.Result = ModelBindingResult.Success(entity);
        } else {
            throw new FgaEntityNotFoundException(context.ModelType, parsedId);
        }
    }

    /// <summary>
    ///     Loads an entity by primary key via <c>DbSet.FindAsync</c> (also returns already-tracked instances).
    /// </summary>
    private static async Task<object?> FindByPrimaryKeyAsync(object dbSet, object parsedId) {
        var findAsyncMethod = dbSet.GetType().GetMethod(nameof(DbSet<>.FindAsync), [typeof(object[])]);
        var findTask = findAsyncMethod!.Invoke(dbSet, [new[] { parsedId }]);
        if (findTask == null) {
            return null;
        }

        // FindAsync returns ValueTask<T>; convert to Task to await, then read Result via reflection.
        var asTaskMethod = findTask.GetType().GetMethod("AsTask");
        if (asTaskMethod != null) {
            var task = (Task) asTaskMethod.Invoke(findTask, null)!;
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        return findTask.GetType().GetProperty("Result")?.GetValue(findTask);
    }

    /// <summary>
    ///     Loads an entity by ID through the LINQ query pipeline so the requested navigation properties can be
    ///     eager-loaded via EF <c>Include</c>.
    /// </summary>
    private static async Task<object?> LoadWithIncludesAsync(
        object dbSet,
        Type entityType,
        Type idClrType,
        object parsedId,
        string[] includes,
        CancellationToken cancellationToken
    ) {
        // Build the predicate: entity => entity.Id == parsedId
        var parameter = Expression.Parameter(entityType, "entity");
        var idProperty = Expression.Property(parameter, nameof(ISealedFgaType<>.Id));
        var idEquals = Expression.Equal(idProperty, Expression.Constant(parsedId, idClrType));
        var lambda = Expression.Lambda(idEquals, parameter);

        var whereMethod = typeof(Queryable).GetMethods()
                                           .First(m => m.Name == nameof(Queryable.Where) &&
                                                       m.GetParameters().Length == 2
                                            )
                                           .MakeGenericMethod(entityType);
        var query = whereMethod.Invoke(null, [dbSet, lambda])!;

        query = ApplyIncludes(query, entityType, includes);

        var firstOrDefaultMethod = typeof(EntityFrameworkQueryableExtensions)
                                  .GetMethods()
                                  .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync) &&
                                              m.GetParameters().Length == 2 &&
                                              m.GetParameters()[1].ParameterType == typeof(CancellationToken)
                                   )
                                  .MakeGenericMethod(entityType);
        var task = (Task) firstOrDefaultMethod.Invoke(null, [query, cancellationToken])!;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
}
