using System.ComponentModel.DataAnnotations.Schema;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Models;

namespace SealedFga.Sample.Secret;

[SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
public partial class SecretEntityId;

public class SecretEntity : ISealedFgaType<SecretEntityId> {
    [SealedFgaRelation(nameof(SecretEntityIdGroups.OwnedBy))]
    public AgencyEntityId OwningAgencyId { get; set; } = null!;

    /// <summary>
    ///     The owning agency navigation. Null unless eager-loaded via
    ///     <c>[FgaAuthorize(..., Include = [nameof(SecretEntityIncludes.OwningAgency)])]</c>.
    /// </summary>
    public AgencyEntity? OwningAgency { get; set; }

    public required string Value { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public SecretEntityId Id { get; set; } = null!;
}
