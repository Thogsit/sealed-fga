using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SealedFga.Attributes;
using SealedFga.Sample.Database;

namespace SealedFga.Sample.Secret;

public record UpdateSecretRequestDto(string Value);

[ApiController]
[Route("secrets")]
public class SecretController(
    SealedFgaSampleContext context
) : ControllerBase {
    [HttpGet]
    public async Task<IActionResult> GetAllSecrets(
        [FgaAuthorizeList(Relation = nameof(SecretEntityIdPermissions.can_view))]
        IQueryable<SecretEntity> secrets
    ) => Ok(await secrets.ToListAsync());

    /// <summary>
    ///     Demonstrates composing onto the injected authorization-filtered query: paging (and the
    ///     ordering it requires) run in the database, on top of the binder's ID filter.
    /// </summary>
    [HttpGet("paged")]
    public async Task<IActionResult> GetSecretsPaged(
        [FgaAuthorizeList(Relation = nameof(SecretEntityIdPermissions.can_view))]
        IQueryable<SecretEntity> secrets,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 10
    ) => Ok(await secrets
                 .OrderBy(s => s.Value)
                 .Skip(page * pageSize)
                 .Take(pageSize)
                 .ToListAsync());

    [HttpGet("{secretId}")]
    public IActionResult GetSecretById(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(
            Relation = nameof(SecretEntityIdPermissions.can_view),
            ParameterName = nameof(secretId),
            Include = [nameof(SecretEntityIncludes.OwningAgency)]
        )]
        SecretEntity secret
    ) => Ok(secret);

    [HttpPut("{secretId}")]
    public async Task<IActionResult> UpdateSecretById(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(Relation = nameof(SecretEntityIdPermissions.can_edit), ParameterName = nameof(secretId))]
        SecretEntity secret,
        [FromBody] UpdateSecretRequestDto updateSecretRequestDto
    ) {
        secret.Value = updateSecretRequestDto.Value;
        await context.SaveChangesAsync();

        return Ok(secret);
    }

    [HttpPost("{secretId}/toggle-agency")]
    public async Task<IActionResult> ToggleAgency(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(Relation = nameof(SecretEntityIdPermissions.can_edit), ParameterName = nameof(secretId))]
        SecretEntity secret
    ) {
        var agencies = await context.AgencyEntities.ToListAsync();
        var newAgency = agencies.First(a => a.Id != secret.OwningAgencyId);

        secret.OwningAgencyId = newAgency.Id;
        await context.SaveChangesAsync();

        return Ok(secret);
    }
}
