using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Fga.Outbox;
using SealedFga.Util;

namespace SealedFga.Fga;

/// <summary>
///     Translates EF Core entity changes into SealedFGA outbox rows. The generated
///     <c>SealedFgaSaveChangesInterceptor</c> calls this from <c>SavingChanges</c>, so the rows are
///     inserted in the <b>same transaction</b> as the entity changes — nothing reaches OpenFGA unless
///     that transaction commits. The rows are later applied to OpenFGA by <see cref="Outbox.SealedFgaOutboxDrainer" />.
///     <para>
///         Two annotation shapes feed this processor (see the attributes' docs for the exact
///         semantics): scalar FK properties via <see cref="SealedFgaRelationAttribute" /> (tuple links
///         the FK value and the entity's own Id) and join entities via the class-level
///         <see cref="SealedFgaJoinRelationAttribute" /> (tuple links two FK properties; the row's own
///         key appears in no tuple).
///     </para>
///     <para>
///         The generated interceptor lives in the consumer's compilation (it is emitted source);
///         this class holds the actual logic so it ships compiled in the runtime library and the
///         emitted interceptor stays a thin forwarding shim.
///     </para>
/// </summary>
public class SealedFgaSaveChangesProcessor {
    /// <summary>
    ///     Per-entity-type sync metadata, resolved via reflection once per type and cached for the
    ///     process lifetime (annotations are compile-time constants).
    /// </summary>
    private static readonly ConcurrentDictionary<Type, EntitySyncPlan> SyncPlans = new();

    /// <summary>
    ///     Main method that processes SealedFGA changes for a given DbContext, enqueuing outbox rows.
    /// </summary>
    /// <param name="context">The DbContext to process changes for.</param>
    public void ProcessSealedFgaChanges(DbContext? context) {
        if (context is null) {
            return;
        }

        // Filters the change tracker for all entries that can possibly change OpenFGA relations
        var sealedFgaEntries = context.ChangeTracker
                                      .Entries()
                                      .Where(e => e.State
                                                      is EntityState.Deleted
                                                         or EntityState.Modified
                                                         or EntityState.Added
                                                  && GetSyncPlan(e.Entity.GetType()).IsRelevant
                                       );

        // Build the outbox rows describing the intended OpenFGA changes
        var outboxEntries = new List<SealedFgaOutboxEntry>();
        foreach (var entry in sealedFgaEntries) {
            ProcessSingleEntityEntry(entry, outboxEntries);
        }

        if (outboxEntries.Count <= 0) {
            return;
        }

        // Enqueue them in the same transaction as the entity changes; the background drainer applies
        // them to OpenFGA once the transaction has committed.
        context.Set<SealedFgaOutboxEntry>().AddRange(outboxEntries);
    }

    /// <summary>
    ///     Processes a single entity entry, appending the resulting outbox rows.
    /// </summary>
    private static void ProcessSingleEntityEntry(
        EntityEntry entry,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        var plan = GetSyncPlan(entry.Entity.GetType());

        if (entry.State == EntityState.Deleted) {
            ProcessDeletedEntity(entry, plan, outboxEntries);
            return;
        }

        // Primary-key changes are unsupported: EF Core throws on key modification of a tracked
        // entity anyway, so real PK changes surface as Deleted + Added — which the Deleted branch
        // above and the per-property emission below already handle.

        foreach (var (attr, propertyName) in plan.ScalarRelations) {
            ProcessEntityPropertyRelation(
                entry.State,
                attr,
                entry.Property(nameof(ISealedFgaType<>.Id)),
                entry.Property(propertyName),
                outboxEntries
            );
        }

        foreach (var join in plan.JoinRelations) {
            ProcessJoinRelation(entry, join, outboxEntries);
        }
    }

    /// <summary>
    ///     Handles a deleted entity: entities with an FGA identity get a purge command for every tuple
    ///     referencing their Id; join relations get a targeted delete of exactly their tuple (the join
    ///     row's own key appears in no tuple, so a purge would be both useless and — being an ordering
    ///     fence backed by store scans — expensive at fan-out scale).
    /// </summary>
    private static void ProcessDeletedEntity(
        EntityEntry entry,
        EntitySyncPlan plan,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        foreach (var join in plan.JoinRelations) {
            var userValue = ValueOf(entry.Property(join.UserProperty));
            var objectValue = ValueOf(entry.Property(join.ObjectProperty));
            if (userValue is null || objectValue is null) {
                continue;
            }

            outboxEntries.Add(SealedFgaOutboxEntry.ForDelete(
                AsTupleString(userValue),
                join.Relation,
                AsTupleString(objectValue)
            ));
        }

        if (!plan.IsSealedFgaType) {
            return;
        }

        // Remove all relations that reference the deleted entity itself
        var idProperty = entry.Property(nameof(ISealedFgaType<>.Id));
        var deletedId = ValueOf(idProperty);
        if (deletedId is null) {
            return;
        }

        outboxEntries.Add(
            SealedFgaOutboxEntry.ForDeleteAllForObject(
                AsTupleString(deletedId),
                IdUtil.GetNameByIdType(deletedId.GetType())
            )
        );
    }

    /// <summary>
    ///     Processes a single join relation for an added or modified entity: the tuple links the two
    ///     FK properties; a tuple is only emitted while both sides are non-null, and a change of
    ///     either side replaces the old pair's tuple with the new pair's.
    /// </summary>
    private static void ProcessJoinRelation(
        EntityEntry entry,
        JoinRelationPlan join,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        var userProperty = entry.Property(join.UserProperty);
        var objectProperty = entry.Property(join.ObjectProperty);

        if (entry.State == EntityState.Added) {
            if (userProperty.CurrentValue is not null && objectProperty.CurrentValue is not null) {
                outboxEntries.Add(SealedFgaOutboxEntry.ForWrite(
                    AsTupleString(userProperty.CurrentValue),
                    join.Relation,
                    AsTupleString(objectProperty.CurrentValue)
                ));
            }

            return;
        }

        // Modified: only process if either FK has actually changed (compared by value, not reference).
        if (IdValuesEqual(userProperty.CurrentValue, userProperty.OriginalValue)
            && IdValuesEqual(objectProperty.CurrentValue, objectProperty.OriginalValue)) {
            return;
        }

        // Delete the previous pair's tuple; if it was complete
        if (userProperty.OriginalValue is not null && objectProperty.OriginalValue is not null) {
            outboxEntries.Add(SealedFgaOutboxEntry.ForDelete(
                AsTupleString(userProperty.OriginalValue),
                join.Relation,
                AsTupleString(objectProperty.OriginalValue)
            ));
        }

        // Write the new pair's tuple; if it is complete
        if (userProperty.CurrentValue is not null && objectProperty.CurrentValue is not null) {
            outboxEntries.Add(SealedFgaOutboxEntry.ForWrite(
                AsTupleString(userProperty.CurrentValue),
                join.Relation,
                AsTupleString(objectProperty.CurrentValue)
            ));
        }
    }

    /// <summary>
    ///     Processes a single property relation for an entity based on its state.
    /// </summary>
    private static void ProcessEntityPropertyRelation(
        EntityState entityState,
        SealedFgaRelationAttribute relationAttribute,
        PropertyEntry entityIdProperty,
        PropertyEntry relationProperty,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        // We're only interested in changes, so disable the "default case missing" warning.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (entityState) {
            case EntityState.Added:
                ProcessAddedEntity(relationAttribute,
                    entityIdProperty,
                    relationProperty,
                    outboxEntries
                );
                break;
            case EntityState.Modified:
                ProcessModifiedEntity(relationAttribute,
                    entityIdProperty,
                    relationProperty,
                    outboxEntries
                );
                break;
        }
    }

    /// <summary>
    ///     Processes relations for a newly added entity.
    /// </summary>
    private static void ProcessAddedEntity(
        SealedFgaRelationAttribute attr,
        PropertyEntry entityIdProperty,
        PropertyEntry relationProperty,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        // If the relation property is null, we don't need to do anything
        if (relationProperty.CurrentValue is null) {
            return;
        }

        var (userTupleStr, objTupleStr) = ExtractTupleStrings(
            attr,
            entityIdProperty.CurrentValue,
            relationProperty.CurrentValue
        );

        outboxEntries.Add(SealedFgaOutboxEntry.ForWrite(userTupleStr, attr.Relation, objTupleStr));
    }

    /// <summary>
    ///     Processes relations for a modified entity.
    /// </summary>
    private static void ProcessModifiedEntity(
        SealedFgaRelationAttribute attr,
        PropertyEntry entityIdProperty,
        PropertyEntry relationProperty,
        List<SealedFgaOutboxEntry> outboxEntries
    ) {
        // Only process if the relation tuple has actually changed (compared by value, not reference).
        if (IdValuesEqual(entityIdProperty.CurrentValue, entityIdProperty.OriginalValue)
            && IdValuesEqual(relationProperty.CurrentValue, relationProperty.OriginalValue)) {
            return;
        }

        // Delete previous relation; if it wasn't null anyway
        if (relationProperty.OriginalValue is not null) {
            var (prevUserTupleStr, prevObjTupleStr) = ExtractTupleStrings(
                attr,
                entityIdProperty.OriginalValue,
                relationProperty.OriginalValue
            );

            outboxEntries.Add(SealedFgaOutboxEntry.ForDelete(prevUserTupleStr, attr.Relation, prevObjTupleStr));
        }

        // Write new relation; if it isn't null anyway
        if (relationProperty.CurrentValue is not null) {
            var (userTupleStr, objTupleStr) = ExtractTupleStrings(
                attr,
                entityIdProperty.CurrentValue,
                relationProperty.CurrentValue
            );

            outboxEntries.Add(SealedFgaOutboxEntry.ForWrite(userTupleStr, attr.Relation, objTupleStr));
        }
    }

    /// <summary>
    ///     Extracts tuple strings from entity and property values via the strongly-typed ID interface.
    /// </summary>
    private static (string userTupleStr, string objTupleStr) ExtractTupleStrings(
        SealedFgaRelationAttribute attr,
        object? entityIdValue,
        object? propertyValue
    ) {
        var propertyOpenFgaTupleString = AsTupleString(propertyValue!);
        var entityOpenFgaTupleString = AsTupleString(entityIdValue!);

        // Switch obj <-> user based on the relation's target type
        return attr.TargetType switch {
            SealedFgaRelationTargetType.Object => (propertyOpenFgaTupleString, entityOpenFgaTupleString),
            SealedFgaRelationTargetType.User => (entityOpenFgaTupleString, propertyOpenFgaTupleString),
            _ => (propertyOpenFgaTupleString, entityOpenFgaTupleString),
        };
    }

    /// <summary>
    ///     Returns the OpenFGA <c>type:id</c> tuple string for a strongly-typed ID value.
    /// </summary>
    private static string AsTupleString(object idValue)
        => ((ISealedFgaUser) idValue).AsOpenFgaIdTupleString();

    /// <summary>
    ///     Reads a property value from a (typically deleted) entry, falling back to the original
    ///     value when the current one is unavailable.
    /// </summary>
    private static object? ValueOf(PropertyEntry property)
        => property.CurrentValue ?? property.OriginalValue;

    /// <summary>
    ///     Compares two ID/relation property values by value (not reference). The strongly-typed IDs
    ///     override <see cref="object.Equals(object)" />, so this correctly treats two distinct
    ///     instances with the same underlying value as equal — avoiding spurious tuple churn.
    /// </summary>
    private static bool IdValuesEqual(object? left, object? right) {
        if (ReferenceEquals(left, right)) {
            return true;
        }

        if (left is null || right is null) {
            return false;
        }

        return left.Equals(right);
    }

    /// <summary>
    ///     Resolves (and caches) the sync plan for an entity type. Misconfigured annotations throw
    ///     here — loud and at the first SaveChanges touching the type, instead of emitting wrong or
    ///     no tuples.
    /// </summary>
    private static EntitySyncPlan GetSyncPlan(Type entityType)
        => SyncPlans.GetOrAdd(entityType, BuildSyncPlan);

    /// <summary>
    ///     Builds the sync plan for an entity type by reflecting over its SealedFGA annotations.
    /// </summary>
    private static EntitySyncPlan BuildSyncPlan(Type entityType) {
        var isSealedFgaType = entityType
                             .GetInterfaces()
                             .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISealedFgaType<>));

        var scalarRelations = entityType
                             .GetProperties()
                             .Select(prop => (Attr: prop.GetCustomAttribute<SealedFgaRelationAttribute>(), prop.Name))
                             .Where(t => t.Attr is not null)
                             .Select(t => (t.Attr!, t.Name))
                             .ToList();

        if (scalarRelations.Count > 0 && !isSealedFgaType) {
            throw new InvalidOperationException(
                $"Entity type '{entityType.Name}' has [SealedFgaRelation] properties but does not "
                + "implement ISealedFgaType<TId>. Scalar FK relations put the entity's own Id on one "
                + "side of the tuple; for a join entity linking two FK properties, use the "
                + "class-level [SealedFgaJoinRelation] instead."
            );
        }

        var joinRelations = entityType
                           .GetCustomAttributes<SealedFgaJoinRelationAttribute>()
                           .Select(attr => new JoinRelationPlan(
                                attr.Relation,
                                ValidateJoinProperty(entityType, attr, attr.UserProperty),
                                ValidateJoinProperty(entityType, attr, attr.ObjectProperty)
                            ))
                           .ToList();

        return new EntitySyncPlan(isSealedFgaType, scalarRelations, joinRelations);
    }

    /// <summary>
    ///     Validates one side of a <see cref="SealedFgaJoinRelationAttribute" />: the named property
    ///     must exist and be a strongly-typed SealedFGA ID.
    /// </summary>
    private static string ValidateJoinProperty(
        Type entityType,
        SealedFgaJoinRelationAttribute attr,
        string propertyName
    ) {
        var property = entityType.GetProperty(propertyName);
        if (property is null) {
            throw new InvalidOperationException(
                $"[SealedFgaJoinRelation(\"{attr.Relation}\")] on entity type '{entityType.Name}' "
                + $"references property '{propertyName}', which does not exist."
            );
        }

        // Struct IDs make optional FKs Nullable<XId>, which itself implements no interfaces —
        // unwrap before checking (the boxed values seen at runtime are plain XId or null).
        var unwrappedType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (!typeof(ISealedFgaUser).IsAssignableFrom(unwrappedType)) {
            throw new InvalidOperationException(
                $"[SealedFgaJoinRelation(\"{attr.Relation}\")] on entity type '{entityType.Name}': "
                + $"property '{propertyName}' has type '{property.PropertyType.Name}', which is not a "
                + "strongly-typed SealedFGA ID (ISealedFgaUser)."
            );
        }

        return property.Name;
    }

    /// <summary>One tuple emission of a join entity: relation plus the two FK property names.</summary>
    private sealed record JoinRelationPlan(string Relation, string UserProperty, string ObjectProperty);

    /// <summary>The cached per-entity-type sync metadata.</summary>
    private sealed record EntitySyncPlan(
        bool IsSealedFgaType,
        List<(SealedFgaRelationAttribute Attr, string PropertyName)> ScalarRelations,
        List<JoinRelationPlan> JoinRelations
    ) {
        /// <summary>Whether SaveChanges on this entity type can affect OpenFGA at all.</summary>
        public bool IsRelevant => IsSealedFgaType || JoinRelations.Count > 0;
    }
}
