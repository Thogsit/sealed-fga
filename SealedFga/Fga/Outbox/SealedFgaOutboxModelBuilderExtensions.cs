using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Registers the SealedFGA outbox entities into an EF Core model.
/// </summary>
public static class SealedFgaOutboxModelBuilderExtensions {
    /// <summary>
    ///     Adds the <see cref="SealedFgaOutboxEntry" /> and <see cref="SealedFgaOutboxLease" />
    ///     entity types to the model, including the indexes backing the drainer's hot queries.
    ///     Provider-agnostic: uses only core configuration (indexes are ignored by non-relational
    ///     providers) so it works on relational and in-memory providers alike.
    /// </summary>
    /// <remarks>
    ///     This is normally invoked automatically via <c>SealedFgaModelCustomizer</c>; it is public so
    ///     it can also be called manually or from tests. Consumers using migrations get the outbox
    ///     tables and indexes in their next <c>dotnet ef migrations add</c>.
    /// </remarks>
    public static ModelBuilder ConfigureSealedFgaOutbox(this ModelBuilder modelBuilder) {
        modelBuilder.Entity<SealedFgaOutboxEntry>(entry => {
            // Claim scan (pending predicate ordered by Id) and retention-sweep cutoff.
            entry.HasIndex(e => new { e.ProcessedAtUtc, e.Id });
            // Newest-wins witness lookups and the sweep's witness-protection subquery.
            entry.HasIndex(e => new { e.TupleObject, e.TupleRelation, e.TupleUser, e.Id });
            // Fence-only blocker and processed-fence lookups.
            entry.HasIndex(e => new { e.OperationType, e.ProcessedAtUtc, e.Id });
        });
        modelBuilder.Entity<SealedFgaOutboxLease>();
        return modelBuilder;
    }
}
