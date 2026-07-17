using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     A durable record of an intended OpenFGA synchronization operation.
///     Rows are inserted in the same DB transaction as the entity changes that caused them
///     (see <c>SealedFgaSaveChangesProcessor</c>) and later applied to OpenFGA by the background
///     drainer (see <c>SealedFgaOutboxDrainer</c>). This makes the DB the source of truth: nothing
///     reaches OpenFGA unless the originating transaction committed.
///     <para>
///         Rows are produced by the annotation interceptor or by the typed
///         <see cref="SealedFgaOutboxEnqueueExtensions" />; the raw string factories are
///         deliberately <c>internal</c> — no public raw-string entry point exists.
///     </para>
/// </summary>
public class SealedFgaOutboxEntry {
    /// <summary>Auto-generated identity; also the strict processing order key.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>When the intent was recorded (UTC). Used for retention/cleanup.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The kind of operation this row represents.</summary>
    public SealedFgaOutboxOperationType OperationType { get; set; }

    // --- Tuple columns (WriteTuple / DeleteTuple) ---

    /// <summary>The tuple's user string, e.g. <c>agency:&lt;id&gt;</c>.</summary>
    public string? TupleUser { get; set; }

    /// <summary>The tuple's relation, e.g. <c>OwnedBy</c>.</summary>
    public string? TupleRelation { get; set; }

    /// <summary>The tuple's object string, e.g. <c>secret:&lt;id&gt;</c>.</summary>
    public string? TupleObject { get; set; }

    // --- Command columns (DeleteAllForObject) ---

    /// <summary>
    ///     For <see cref="SealedFgaOutboxOperationType.DeleteAllForObject" />: the object tuple string
    ///     (<c>type:id</c>) to purge.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>The OpenFGA type name of the target (for the command operations).</summary>
    public string? TypeName { get; set; }

    // --- Processing bookkeeping ---

    /// <summary>Number of failed apply attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest UTC time the row may be retried (null = eligible immediately).</summary>
    public DateTime? NextAttemptUtc { get; set; }

    /// <summary>When the row was successfully applied to OpenFGA (null = still pending).</summary>
    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>The last apply error message, for diagnostics.</summary>
    public string? LastError { get; set; }

    /// <summary>Creates a <see cref="SealedFgaOutboxOperationType.WriteTuple" /> entry.</summary>
    internal static SealedFgaOutboxEntry ForWrite(string user, string relation, string obj)
        => new() {
            CreatedAtUtc = DateTime.UtcNow,
            OperationType = SealedFgaOutboxOperationType.WriteTuple,
            TupleUser = user,
            TupleRelation = relation,
            TupleObject = obj,
        };

    /// <summary>Creates a <see cref="SealedFgaOutboxOperationType.DeleteTuple" /> entry.</summary>
    internal static SealedFgaOutboxEntry ForDelete(string user, string relation, string obj)
        => new() {
            CreatedAtUtc = DateTime.UtcNow,
            OperationType = SealedFgaOutboxOperationType.DeleteTuple,
            TupleUser = user,
            TupleRelation = relation,
            TupleObject = obj,
        };

    /// <summary>Creates a <see cref="SealedFgaOutboxOperationType.DeleteAllForObject" /> entry.</summary>
    internal static SealedFgaOutboxEntry ForDeleteAllForObject(string targetId, string typeName)
        => new() {
            CreatedAtUtc = DateTime.UtcNow,
            OperationType = SealedFgaOutboxOperationType.DeleteAllForObject,
            TargetId = targetId,
            TypeName = typeName,
        };
}
