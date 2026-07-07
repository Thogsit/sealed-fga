using System.Threading.Tasks;

namespace SealedFga.Sample.Secret;

public interface ISecretService {
    Task<SecretEntity?> GetSecretByIdAsync(SecretEntityId secretId);
}
