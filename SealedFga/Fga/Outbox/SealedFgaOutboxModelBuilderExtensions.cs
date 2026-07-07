using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Registers the SealedFGA outbox entity into an EF Core model.
/// </summary>
public static class SealedFgaOutboxModelBuilderExtensions {
    /// <summary>
    ///     Adds the <see cref="SealedFgaOutboxEntry" /> entity type to the model. Provider-agnostic:
    ///     uses only core (non-relational) configuration so it works on relational and in-memory
    ///     providers alike.
    /// </summary>
    /// <remarks>
    ///     This is normally invoked automatically via <c>SealedFgaModelCustomizer</c>; it is public so
    ///     it can also be called manually or from tests.
    /// </remarks>
    public static ModelBuilder ConfigureSealedFgaOutbox(this ModelBuilder modelBuilder) {
        // IMPORTANT: this runs from the netstandard2.0 runtime library but is compiled against an
        // older EF Core API surface than the consumer runs on. Only the stable `Entity<T>()` entry
        // point is safe to call here — the fluent builder methods (HasKey/HasIndex/HasConversion)
        // have signatures that drift between EF versions and throw MissingMethodException at runtime.
        // Everything else is expressed via version-independent data annotations on the entity, or
        // left to EF conventions (Id => key + identity; enum => int).
        modelBuilder.Entity<SealedFgaOutboxEntry>();
        return modelBuilder;
    }
}
