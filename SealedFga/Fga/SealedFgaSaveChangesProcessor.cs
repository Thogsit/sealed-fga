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
///         Due to the restriction to netstandard 2.0, this class is decoupled from the generated
///         interceptor (which must inherit an EF Core base type the generator project cannot reference).
///     </para>
/// </summary>
public class SealedFgaSaveChangesProcessor {
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
                                                  && e.Entity.GetType().GetInterfaces()
                                                      .Any(i =>
                                                           i.IsGenericType && i.GetGenericTypeDefinition() ==
                                                           typeof(ISealedFgaType<>)
                                                       )
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
        var entityType = entry.Entity.GetType();
        var entityIdProperty = entry.Property(nameof(ISealedFgaType<>.Id));

        // If the entity is deleted, we need to remove all relations that use it
        if (entry.State == EntityState.Deleted) {
            var deletedId = entityIdProperty.CurrentValue ?? entityIdProperty.OriginalValue;
            if (deletedId is null) {
                return;
            }

            outboxEntries.Add(
                SealedFgaOutboxEntry.ForDeleteAllForObject(
                    AsTupleString(deletedId),
                    IdUtil.GetNameByIdType(deletedId.GetType())
                )
            );
            return;
        }

        // If a Modified entity's primary key changed, rewrite every tuple referencing the old ID.
        // We handle this via a single ModifyId row and skip per-property emission for this entity to
        // avoid double-handling the same relation tuples. (The rare simultaneous PK+FK change is not
        // fully handled; PK changes are uncommon since EF usually models them as delete + insert.)
        if (entry.State == EntityState.Modified
            && !IdValuesEqual(entityIdProperty.CurrentValue, entityIdProperty.OriginalValue)) {
            var oldId = entityIdProperty.OriginalValue;
            var newId = entityIdProperty.CurrentValue;
            if (oldId is not null && newId is not null) {
                outboxEntries.Add(
                    SealedFgaOutboxEntry.ForModifyId(
                        AsTupleString(oldId),
                        AsTupleString(newId),
                        IdUtil.GetNameByIdType(oldId.GetType())
                    )
                );
            }

            return;
        }

        foreach (var property in entityType
                                .GetProperties()
                                .Where(prop => prop.GetCustomAttribute<SealedFgaRelationAttribute>() != null)) {
            var attr = property.GetCustomAttribute<SealedFgaRelationAttribute>()!;
            var entryProperty = entry.Property(property.Name);

            ProcessEntityPropertyRelation(
                entry.State,
                attr,
                entityIdProperty,
                entryProperty,
                outboxEntries
            );
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
}
