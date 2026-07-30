using System;
using System.Text;

namespace SealedFga.Npgsql;

/// <summary>
///     Configuration for the PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> transport that carries outbox
///     wake-ups between replicas.
/// </summary>
public class SealedFgaNpgsqlOutboxOptions {
    private string _channel = "sealedfga_outbox";

    /// <summary>
    ///     The <c>NOTIFY</c> channel both sides use. Only needs changing when several independent
    ///     SealedFGA deployments share one database — they would otherwise wake each other for work
    ///     the other cannot see, which is harmless but wasteful.
    ///     <para>
    ///         Restricted to <c>[A-Za-z0-9_]</c> and PostgreSQL's 63-byte identifier limit. That is
    ///         narrower than PostgreSQL allows, but it lets the <c>LISTEN</c> statement — whose
    ///         channel is an identifier, not a parameter — be built without quoting or escaping.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, too long, or has disallowed characters.</exception>
    public string Channel {
        get => _channel;
        set => _channel = Validated(value);
    }

    /// <summary>
    ///     Connection string for the listener's dedicated connection. Defaults to the one the
    ///     consumer's <c>DbContext</c> uses, which is almost always right — the listener has to reach
    ///     the same database the outbox lives in. Override when the listener should use different
    ///     credentials or a direct host, most notably to route around a
    ///     <b>transaction-mode connection pooler</b>: those multiplex one server connection across
    ///     clients and silently break <c>LISTEN</c> (the session that holds the subscription is not
    ///     the one a later statement lands on). Session-mode pooling and direct connections are fine.
    /// </summary>
    public string? ListenerConnectionString { get; set; }

    /// <summary>
    ///     How often Npgsql pokes the idle listening connection to prove it is still alive.
    ///     <para>
    ///         Not optional in practice: a <c>LISTEN</c> connection sends nothing for as long as the
    ///         outbox is quiet, so firewalls, NAT tables and load balancers reap it — and a silently
    ///         dead socket never raises an error, it just stops delivering. Without a keep-alive the
    ///         failure mode is invisible: notifications stop, the drainer quietly falls back to
    ///         polling, and nothing in the logs says so. Set to <see cref="TimeSpan.Zero" /> to
    ///         disable (not recommended).
    ///     </para>
    /// </summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before the first reconnect attempt after the listener loses its connection.</summary>
    public TimeSpan ReconnectMinBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling the reconnect delay doubles up to while the database stays unreachable.</summary>
    public TimeSpan ReconnectMaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    private static string Validated(string value) {
        if (string.IsNullOrEmpty(value)) {
            throw new ArgumentException("The NOTIFY channel name must not be empty.", nameof(value));
        }

        if (Encoding.UTF8.GetByteCount(value) > 63) {
            throw new ArgumentException(
                $"The NOTIFY channel name '{value}' exceeds PostgreSQL's 63-byte identifier limit.",
                nameof(value)
            );
        }

        foreach (var c in value) {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')) {
                throw new ArgumentException(
                    $"The NOTIFY channel name '{value}' may only contain letters, digits and underscores.",
                    nameof(value)
                );
            }
        }

        return value;
    }
}
