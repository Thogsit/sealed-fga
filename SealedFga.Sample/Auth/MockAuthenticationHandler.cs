using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SealedFga.Sample.Auth;

/// <summary>
///     Only used to inject a mock user for testing.
/// </summary>
public class MockAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
    /// <summary>
    ///     When this request header is present, the ticket is still authenticated but carries no
    ///     <c>open_fga_user</c> claim — lets integration tests exercise the binders' claim lookup.
    /// </summary>
    public const string OmitUserClaimHeader = "X-Test-Omit-Fga-User-Claim";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
        var claims = Request.Headers.ContainsKey(OmitUserClaimHeader)
            ? []
            : new[] { new Claim("open_fga_user", "user:some-id") };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
