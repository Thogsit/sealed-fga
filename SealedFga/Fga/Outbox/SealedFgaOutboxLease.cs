using System;
using System.ComponentModel.DataAnnotations;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Single-row coordination table electing the one active outbox drainer per database: a
///     replica drains only while it holds this lease (see <c>SealedFgaOutboxLeaseManager</c>).
///     Managed entirely by SealedFGA; consumers only need to include it in their migrations.
/// </summary>
public class SealedFgaOutboxLease {
    /// <summary>The lease's well-known name; the drainer lease is the only row.</summary>
    [Key]
    public required string Name { get; set; }

    /// <summary>Opaque id of the replica currently holding the lease; null before first acquisition.</summary>
    public string? HolderId { get; set; }

    /// <summary>
    ///     UTC instant the lease expires. The holder renews it ahead of every drain pass; other
    ///     replicas take over once it lapses (crash takeover ≤ lease duration + poll interval).
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
