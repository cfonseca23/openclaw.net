namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Optional channel capability for reconnecting or restarting an adapter at runtime.
/// </summary>
public interface IRestartableChannelAdapter
{
    Task RestartAsync(CancellationToken ct);
}
