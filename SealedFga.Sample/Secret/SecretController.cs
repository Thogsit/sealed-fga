using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SealedFga.Attributes;
using SealedFga.Fga;
using SealedFga.Sample.Database;
using SealedFga.Sample.User;

namespace SealedFga.Sample.Secret;

public record UpdateSecretRequestDto(string Value);

[ApiController]
[Route("secrets")]
public class SecretController(
    SealedFgaSampleContext context,
    SealedFgaService sealedFgaService,
    ISecretService secretService
) : ControllerBase {
    [HttpGet]
    public IActionResult GetAllSecrets(
        [FgaAuthorizeList(Relation = nameof(SecretEntityIdAttributes.can_view))]
        List<SecretEntity> secrets
    ) => Ok(secrets);

    [HttpGet("{secretId}")]
    public IActionResult GetSecretById(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(
            Relation = nameof(SecretEntityIdAttributes.can_view),
            ParameterName = nameof(secretId),
            Include = [nameof(SecretEntityIncludes.OwningAgency)]
        )]
        SecretEntity secret
    ) => Ok(secret);

    [HttpPut("{secretId}")]
    public async Task<IActionResult> UpdateSecretById(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(Relation = nameof(SecretEntityIdAttributes.can_edit), ParameterName = nameof(secretId))]
        SecretEntity secret,
        [FromBody] UpdateSecretRequestDto updateSecretRequestDto
    ) {
        secret.Value = updateSecretRequestDto.Value;
        await context.SaveChangesAsync();

        var secrets = await context.SecretEntities.ToListAsync();
        var secretsSync = context.SecretEntities.ToList();

        //var someSecret = secretService.GetSecretByIdAsync(secretId);

        return Ok(secret);
    }

    [HttpPost("{secretId}/toggle-agency")]
    public async Task<IActionResult> ToggleAgency(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(Relation = nameof(SecretEntityIdAttributes.can_edit), ParameterName = nameof(secretId))]
        SecretEntity secret
    ) {
        var agencies = await context.AgencyEntities.ToListAsync();
        var newAgency = agencies.First(a => a.Id != secret.OwningAgencyId);

        // Create a copy; should receive all checked permissions
        var newId = secretId;

        //SealedFgaGuard.RequireCheck(newId, SecretEntityIdAttributes.can_view, SecretEntityIdAttributes.can_edit);
        //SealedFgaGuard.RequireCheck(secret, SecretEntityIdAttributes.can_edit);
        //SealedFgaGuard.RequireCheck(secretId, SecretEntityIdAttributes.can_edit, SecretEntityIdAttributes.can_view);

        secret.OwningAgencyId = newAgency.Id;
        await context.SaveChangesAsync();

        return Ok(secret);
    }

    /*
     * New Tests
     */

    [HttpGet("{secretId}/1")]
    public async Task<IActionResult> GetSecretById1(
        [FromRoute] SecretEntityId secretId,
        [FgaAuthorize(Relation = nameof(SecretEntityIdAttributes.can_view), ParameterName = nameof(secretId))]
        SecretEntity secret
    ) {
        //var newId = SecretEntityId.New();
        //if (secret.Id == newId) {
        //}
        await sealedFgaService.EnsureCheckAsync(UserEntityId.New(), SecretEntityIdAttributes.can_edit, secretId);

        //await secretService.GetSecretByIdAsync(secretId);
        SealedFgaGuard.RequireCheck(secretId, SecretEntityIdAttributes.can_edit);

        return Ok(secret);
    }
}
