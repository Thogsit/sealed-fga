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

    /// <summary>Presence grants the list binder full (unfiltered) access — the "super-user" escape hatch.</summary>
    public const string FullAccessHeaderName = "X-Test-Full-Access";

    /// <summary>Value = comma-separated raw secret IDs the list binder should be scoped to.</summary>
    public const string ScopeIdsHeaderName = "X-Test-Scope-Ids";

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

    public async ValueTask<SealedFgaListVerdict> GetListVerdictAsync(SealedFgaBinderOptionsContext context) {
        if (context.HttpContext.Request.Headers.ContainsKey(FullAccessHeaderName)) {
            return SealedFgaListVerdict.FullAccess;
        }

        if (context.HttpContext.Request.Headers.TryGetValue(ScopeIdsHeaderName, out var scopeIds)) {
            return SealedFgaListVerdict.ScopedToIds(
                scopeIds.SelectMany(v => v!.Split(',')).ToList()
            );
        }

        // Otherwise keep the default behavior (ListObjects with any contextual-tuple options).
        return SealedFgaListVerdict.Normal(await GetOptionsAsync(context));
    }
}
