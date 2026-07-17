using System.Linq;
using System.Threading.Tasks;
using SealedFga.Fga;
using SealedFga.ModelBinder;
using SealedFga.Sample.Secret;
using SealedFga.Sample.User;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Test <see cref="ISealedFgaBinderOptionsProvider" />: when the request carries
///     <see cref="HeaderName" /> (value = a secret's GUID), it grants the current user a contextual
///     <c>can_view</c> tuple on that secret — the same shape as a real "super-user" provider
///     that derives contextual tuples from the request's identity. Requests without the
///     header get <c>null</c>, i.e. unchanged binder behavior.
/// </summary>
internal sealed class HeaderContextualTupleProvider : ISealedFgaBinderOptionsProvider {
    public const string HeaderName = "X-Test-Contextual-View-Secret";

    public ValueTask<SealedFgaQueryOptions?> GetOptionsAsync(SealedFgaBinderOptionsContext context) {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var secretIds)) {
            return ValueTask.FromResult<SealedFgaQueryOptions?>(null);
        }

        // RawUser is the OpenFGA tuple string ("user:some-id"); recover the typed ID for Of(...).
        var user = UserEntityId.Parse(context.RawUser[(context.RawUser.IndexOf(':') + 1)..]);
        return ValueTask.FromResult<SealedFgaQueryOptions?>(new SealedFgaQueryOptions {
            ContextualTuples = secretIds
                              .Select(id => SealedFgaContextualTuple.Of(
                                   user,
                                   SecretEntityIdPermissions.can_view,
                                   SecretEntityId.Parse(id!)
                               ))
                              .ToList(),
        });
    }
}
