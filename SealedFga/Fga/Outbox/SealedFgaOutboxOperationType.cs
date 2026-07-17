namespace SealedFga.Fga.Outbox;

/// <summary>
///     The kind of OpenFGA synchronization operation an <see cref="SealedFgaOutboxEntry" /> represents.
/// </summary>
public enum SealedFgaOutboxOperationType {
    /// <summary>Write a single relationship tuple (idempotent).</summary>
    WriteTuple = 0,

    /// <summary>Delete a single relationship tuple (idempotent).</summary>
    DeleteTuple = 1,

    /// <summary>
    ///     Delete every relationship tuple in which the target object appears (as object or as user).
    ///     The concrete set of tuples is resolved against OpenFGA at drain time.
    /// </summary>
    DeleteAllForObject = 2,
}
