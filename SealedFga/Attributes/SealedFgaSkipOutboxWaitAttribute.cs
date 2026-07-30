using System;

namespace SealedFga.Attributes;

/// <summary>
///     Opts an action or controller out of the end-of-request outbox wait
///     (<see cref="SealedFgaOptions.WaitForOutboxOnRequestEnd" />).
///     <para>
///         Use it for endpoints whose tuple changes nothing reads back immediately — bulk imports,
///         fan-outs, fire-and-forget maintenance calls — where the wait would add latency for no
///         benefit. Do <b>not</b> use it to speed up an endpoint whose result the client then reads:
///         that is exactly the race the wait exists to close, and the failure it produces is an
///         intermittent 403 rather than an obviously missing row.
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class SealedFgaSkipOutboxWaitAttribute : Attribute;
