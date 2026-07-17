using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     An <see cref="IModelCustomizer" /> decorator that auto-registers the SealedFGA outbox entity
///     into the consumer's model, so they don't have to touch their <c>OnModelCreating</c>.
/// </summary>
/// <remarks>
///     Derives from the provider-agnostic base <see cref="ModelCustomizer" /> (not the relational
///     variant): relational model finalization happens later via EF conventions, so nothing
///     relational is lost by using this base, and the same customizer works on in-memory providers.
///     Wired up via <c>DbContextOptionsBuilder.ReplaceService&lt;IModelCustomizer, SealedFgaModelCustomizer&gt;()</c>
///     inside the generated <c>AddSealedFga</c> extension.
/// </remarks>
public class SealedFgaModelCustomizer(ModelCustomizerDependencies dependencies)
    : ModelCustomizer(dependencies) {
    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context) {
        // Runs the consumer's OnModelCreating (and DbSet discovery) first ...
        base.Customize(modelBuilder, context);
        // ... then adds our outbox entity.
        modelBuilder.ConfigureSealedFgaOutbox();
    }
}
