using OpenClaw.Core.Models;

namespace OpenClaw.Core.Plugins;

public static class PluginCapabilityPolicy
{
    public enum ExecutionHostKind
    {
        Bridge,
        NativeDynamic
    }

    public const string Tools = "tools";
    public const string Services = "services";
    public const string Skills = "skills";
    public const string Channels = "channels";
    public const string Commands = "commands";
    public const string Providers = "providers";
    public const string Hooks = "hooks";
    public const string Memory = "memory";
    public const string NativeDynamic = "native_dynamic";

    public static string[] Normalize(IEnumerable<string> capabilities)
        => capabilities
            .Where(cap => !string.IsNullOrWhiteSpace(cap))
            .Select(cap => cap.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cap => cap, StringComparer.Ordinal)
            .ToArray();

    public static string[] GetBlockedCapabilities(
        GatewayRuntimeMode runtimeMode,
        IEnumerable<string> capabilities,
        ExecutionHostKind hostKind)
    {
        var normalized = Normalize(capabilities);
        if (runtimeMode != GatewayRuntimeMode.Aot)
            return [];

        return hostKind switch
        {
            // Bridge plugins execute out-of-process behind a typed JSON-RPC boundary,
            // so the existing bridge surfaces are AOT-safe.
            ExecutionHostKind.Bridge => [],

            // Dynamic native plugins depend on reflection and runtime loading.
            ExecutionHostKind.NativeDynamic => normalized,
            _ => normalized
        };
    }

    public static bool RequiresJit(
        GatewayRuntimeMode runtimeMode,
        IEnumerable<string> capabilities,
        ExecutionHostKind hostKind)
        => GetBlockedCapabilities(runtimeMode, capabilities, hostKind).Length > 0;
}
