using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Provides centralized OpenTelemetry instances (ActivitySource, Meters) for the OpenClaw application.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "OpenClaw.Gateway";
    public const string Version = "1.0.0";

    /// <summary>
    /// The primary ActivitySource for distributed tracing within OpenClaw.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, Version);

    /// <summary>
    /// The primary Meter for OpenClaw metrics. Registered with the OTLP exporter via
    /// GatewayTelemetryExtensions.AddGatewayTelemetry (meter name "OpenClaw.Gateway").
    /// </summary>
    public static readonly Meter Meter = new(ServiceName, Version);

    /// <summary>
    /// Histogram recording the duration (ms) of each tool execution, tagged by tool name and success.
    /// </summary>
    public static readonly Histogram<double> ToolExecutionDuration =
        Meter.CreateHistogram<double>(
            "openclaw.tool.execution.duration",
            unit: "ms",
            description: "Duration of tool executions in milliseconds");

    /// <summary>
    /// Counter incremented when a request is blocked by rate limiting, tagged by actor type and policy.
    /// </summary>
    public static readonly Counter<long> RateLimitExceeded =
        Meter.CreateCounter<long>(
            "openclaw.ratelimit.exceeded",
            description: "Number of requests blocked by rate limiting");

    /// <summary>
    /// Registers an observable gauge that reports the current pending approval queue depth.
    /// Call once during startup after the ToolApprovalService is constructed.
    /// </summary>
    public static void RegisterApprovalQueueGauge(Func<int> observeFunc)
    {
        Meter.CreateObservableGauge(
            "openclaw.approval.queue.depth",
            observeFunc,
            description: "Number of pending tool approval requests");
    }
}
