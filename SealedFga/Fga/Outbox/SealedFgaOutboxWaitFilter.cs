using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SealedFga.Attributes;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Holds a request open until the tuple changes it enqueued have reached OpenFGA, so a client
///     that immediately reads back what it just created cannot lose the race.
///     <para>
///         It runs <b>after the action</b> rather than inside the save, which is the only workable
///         place: at <c>SavedChanges</c> an explicit transaction opened by the action is still open,
///         so the rows are invisible to the drainer and waiting there would deadlock against it. By
///         the time the action returns, every transaction it opened has committed.
///     </para>
///     <para>
///         Applied globally on purpose. Most enqueues are attribute-driven — a
///         <c>[SealedFgaRelation]</c> on a foreign key — and so are invisible at the call site, which
///         makes opt-in coverage silently incomplete the moment someone annotates a new entity.
///         Endpoints that genuinely need no wait opt out with
///         <see cref="SealedFgaSkipOutboxWaitAttribute" />.
///     </para>
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
/// <param name="waiter">The scope's waiter.</param>
/// <param name="options">Supplies the on/off switch.</param>
/// <param name="logger">Reports waits that timed out.</param>
public class SealedFgaOutboxWaitFilter<TDbContext>(
    SealedFgaOutboxWaiter<TDbContext> waiter,
    IOptions<SealedFgaOptions> options,
    ILogger<SealedFgaOutboxWaitFilter<TDbContext>> logger
) : IAsyncActionFilter
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next) {
        var executed = await next();

        if (!options.Value.WaitForOutboxOnRequestEnd || IsOptedOut(context)) {
            return;
        }

        // A failed action's transaction rolled back, so there is nothing to wait for — and its rows,
        // if any were tracked, were never committed.
        if (executed.Exception is not null && !executed.ExceptionHandled) {
            return;
        }

        if (!await waiter.WaitForPendingAsync(context.HttpContext.RequestAborted)) {
            logger.LogWarning(
                "Outbox rows enqueued by {Method} {Path} were not applied within the wait timeout; a "
                + "read-back of the affected entity may briefly fail its permission check.",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path
            );
        }
    }

    private static bool IsOptedOut(ActionContext context)
        => context.ActionDescriptor.EndpointMetadata.OfType<SealedFgaSkipOutboxWaitAttribute>().Any();
}
