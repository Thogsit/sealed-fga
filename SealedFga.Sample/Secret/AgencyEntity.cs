using System.ComponentModel.DataAnnotations.Schema;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Models;

namespace SealedFga.Sample.Secret;

[SealedFgaTypeId("agency", SealedFgaTypeIdType.Guid)]
public readonly partial record struct AgencyEntityId;

public class AgencyEntity : ISealedFgaType<AgencyEntityId> {
    public required string Name { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public AgencyEntityId Id { get; set; }
}
