using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using SealedFga.Util;

namespace SealedFga.ModelBinder;

/// <summary>
///     Shared reflection lookups for the model binders, resolved once (or once per type) instead of
///     on every request. Deliberately non-generic: statics on the generic binder base would be
///     duplicated per closed attribute type. Reflection is used because the binders dispatch on
///     entity/ID <see cref="Type" />s only known at runtime (closing generic EF/LINQ methods over
///     them), not to avoid binding to EF Core.
///     Lookups that can only fail through consumer misconfiguration throw
///     <see cref="InvalidOperationException" /> from inside the cache factory (failures are not
///     cached, which is harmless: they are only fixable by recompiling anyway).
/// </summary>
internal static class SealedFgaBinderReflectionCache {
    /// <summary>
    ///     The open generic <c>EntityFrameworkQueryableExtensions.Include&lt;TEntity&gt;(IQueryable&lt;TEntity&gt;, string)</c>
    ///     method. The string overload is used so callers can pass navigation property paths.
    /// </summary>
    private static readonly MethodInfo OpenIncludeMethod =
        typeof(EntityFrameworkQueryableExtensions)
           .GetMethods()
           .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.Include)
                       && m.IsGenericMethodDefinition
                       && m.GetGenericArguments().Length == 1
                       && m.GetParameters().Length == 2
                       && m.GetParameters()[1].ParameterType == typeof(string)
            );

    /// <summary>The open generic <c>Queryable.Where&lt;T&gt;(IQueryable&lt;T&gt;, Expression&lt;Func&lt;T, bool&gt;&gt;)</c> method.</summary>
    private static readonly MethodInfo OpenWhereMethod =
        typeof(Queryable).GetMethods()
                         .First(m => m.Name == nameof(Queryable.Where) && m.GetParameters().Length == 2);

    /// <summary>The open generic <c>Enumerable.Contains&lt;T&gt;(IEnumerable&lt;T&gt;, T)</c> method.</summary>
    private static readonly MethodInfo OpenContainsMethod =
        typeof(Enumerable).GetMethods()
                          .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    /// <summary>The open generic <c>EntityFrameworkQueryableExtensions.FirstOrDefaultAsync&lt;T&gt;(IQueryable&lt;T&gt;, CancellationToken)</c> method.</summary>
    private static readonly MethodInfo OpenFirstOrDefaultAsyncMethod =
        typeof(EntityFrameworkQueryableExtensions)
           .GetMethods()
           .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)
                       && m.GetParameters().Length == 2
                       && m.GetParameters()[1].ParameterType == typeof(CancellationToken)
            );

    /// <summary>The open generic parameterless <c>DbContext.Set&lt;TEntity&gt;()</c> method.</summary>
    private static readonly MethodInfo OpenSetMethod =
        typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> IncludeByEntityType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> WhereByEntityType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> ContainsByIdType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> FirstOrDefaultAsyncByEntityType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> SetByEntityType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> FindAsyncByDbSetType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> TupleStringMethodByIdType = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> TypeNamePropertyByIdType = new();

    internal static MethodInfo Include(Type entityType)
        => IncludeByEntityType.GetOrAdd(entityType, static t => OpenIncludeMethod.MakeGenericMethod(t));

    internal static MethodInfo Where(Type entityType)
        => WhereByEntityType.GetOrAdd(entityType, static t => OpenWhereMethod.MakeGenericMethod(t));

    internal static MethodInfo Contains(Type idType)
        => ContainsByIdType.GetOrAdd(idType, static t => OpenContainsMethod.MakeGenericMethod(t));

    internal static MethodInfo FirstOrDefaultAsync(Type entityType)
        => FirstOrDefaultAsyncByEntityType.GetOrAdd(
            entityType,
            static t => OpenFirstOrDefaultAsyncMethod.MakeGenericMethod(t)
        );

    internal static MethodInfo Set(Type entityType)
        => SetByEntityType.GetOrAdd(entityType, static t => OpenSetMethod.MakeGenericMethod(t));

    /// <summary>The <c>FindAsync(object[])</c> method of a concrete <c>DbSet</c> type.</summary>
    internal static MethodInfo FindAsync(Type dbSetType)
        => FindAsyncByDbSetType.GetOrAdd(
            dbSetType,
            static t => t.GetMethod(nameof(DbSet<>.FindAsync), [typeof(object[])])
                        ?? throw new InvalidOperationException(
                            $"'{t}' does not expose a FindAsync(object[]) method."
                        )
        );

    /// <summary>The generated <c>AsOpenFgaIdTupleString()</c> method of a strongly-typed ID type.</summary>
    internal static MethodInfo OpenFgaIdTupleStringMethod(Type idType)
        => TupleStringMethodByIdType.GetOrAdd(
            idType,
            static t => t.GetMethod(GeneratedNames.OpenFgaIdTupleStringMethodName)
                        ?? throw new InvalidOperationException(
                            $"ID type '{t.Name}' has no '{GeneratedNames.OpenFgaIdTupleStringMethodName}' method. "
                            + "Is the type annotated with [SealedFgaTypeId] and has the source generator run?"
                        )
        );

    /// <summary>The generated static <c>OpenFgaTypeName</c> property of a strongly-typed ID type.</summary>
    internal static PropertyInfo OpenFgaTypeNameProperty(Type idType)
        => TypeNamePropertyByIdType.GetOrAdd(
            idType,
            static t => t.GetProperty(
                            GeneratedNames.OpenFgaTypeNamePropertyName,
                            BindingFlags.Public | BindingFlags.Static
                        )
                        ?? throw new InvalidOperationException(
                            $"ID type '{t.Name}' has no static '{GeneratedNames.OpenFgaTypeNamePropertyName}' property. "
                            + "Is the type annotated with [SealedFgaTypeId] and has the source generator run?"
                        )
        );
}
