namespace OpenClaw.Agent.Plugins;

public interface IPluginRuntimeTelemetrySource
{
    bool TryGetRestartCount(string pluginId, out int restartCount);
    bool TryGetMemorySnapshot(string pluginId, out PluginBridgeMemorySnapshot? snapshot);
}
