using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Models;

namespace SealedFga.PackagingTests;

// A realistic consumer: empty partial ID classes completed by the generator, and an entity with a
// [SealedFgaRelation] foreign key — the same shape the sample uses.

[SealedFgaTypeId("widget", SealedFgaTypeIdType.Guid)]
public partial class WidgetEntityId;

[SealedFgaTypeId("owner", SealedFgaTypeIdType.Guid)]
public partial class OwnerEntityId;

public class WidgetEntity : ISealedFgaType<WidgetEntityId> {
    [SealedFgaRelation(nameof(WidgetEntityIdGroups.OwnedBy))]
    public OwnerEntityId OwnerId { get; set; } = null!;

    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public WidgetEntityId Id { get; set; } = null!;
}

/// <summary>A consumer DbContext wired exactly as documented: value converters via the generated
/// <c>ConfigureSealedFga()</c>; the outbox entity is added automatically by the model customizer.</summary>
public class ConsumerContext(DbContextOptions<ConsumerContext> options) : DbContext(options) {
    public DbSet<WidgetEntity> Widgets => Set<WidgetEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ConfigureSealedFga();
}
