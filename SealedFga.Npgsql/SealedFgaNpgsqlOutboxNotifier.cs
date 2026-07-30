using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SealedFga.Fga.Outbox;

namespace SealedFga.Npgsql;

/// <summary>
///     Publishes outbox wake-ups as PostgreSQL <c>NOTIFY</c> messages, so the replica holding the
///     drainer lease reacts to an enqueue that happened on a different replica.
/// </summary>
/// <remarks>
///     <para>
///         The statement runs on the <b>context's own connection</b>, and that is the whole trick.
///         PostgreSQL queues notifications and delivers them at commit, so riding the caller's
///         connection makes delivery inherit the caller's transaction outcome for free:
///     </para>
///     <list type="bullet">
///         <item>plain <c>SaveChanges</c> — no enclosing transaction, so the notify commits with the statement and is delivered at once;</item>
///         <item>inside an explicit transaction — held until the consumer commits, which is exactly when the rows become visible to the drainer;</item>
///         <item>rolled back — never delivered, so nobody is woken for rows that no longer exist.</item>
///     </list>
///     <para>
///         Using a separate connection would get the middle case wrong: it would fire while the rows
///         were still invisible to everyone else, the leader would drain, find nothing, and go back
///         to sleep — precisely the bug this exists to fix. It also means the notifier is indifferent
///         to whether EF happens to raise <c>SavedChanges</c> inside or outside its implicit
///         transaction, since both orderings land on a correct branch above.
///     </para>
/// </remarks>
public class SealedFgaNpgsqlOutboxNotifier(
    IOptions<SealedFgaNpgsqlOutboxOptions> options,
    ILogger<SealedFgaNpgsqlOutboxNotifier> logger
) : ISealedFgaOutboxNotifier {
    /// <inheritdoc />
    public async Task NotifyAsync(DbContext context, CancellationToken cancellationToken = default) {
        var channel = options.Value.Channel;
        try {
            // Parameterized rather than interpolated: pg_notify takes the channel as a value, which
            // keeps the name out of the SQL text entirely.
            await context.Database.ExecuteSqlRawAsync(
                "SELECT pg_notify({0}, '')",
                [channel],
                cancellationToken
            );
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best-effort by contract: the rows are committed and the drainer's poll will still find
            // them, so this is a latency regression to investigate, not a failed write.
            logger.LogWarning(
                ex,
                "SealedFGA could not publish an outbox NOTIFY on channel {Channel}; other replicas "
                + "will only pick the rows up on their next poll.",
                channel
            );
        }
    }
}
