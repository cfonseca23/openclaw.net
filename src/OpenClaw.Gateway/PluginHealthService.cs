using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

internal sealed class PluginHealthService
{
    private const string DirectoryName = "admin";
    private const string FileName = "plugin-state.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<PluginHealthService> _logger;
    private readonly PluginsConfig _pluginsConfig;
    private List<PluginOperatorState>? _cachedState;
    private IReadOnlyList<PluginLoadReport> _reports = [];
    private IPluginRuntimeTelemetrySource? _pluginRuntimeTelemetry;
    private IPluginRuntimeTelemetrySource? _nativeDynamicPluginRuntimeTelemetry;

    public PluginHealthService(string storagePath, ILogger<PluginHealthService> logger, PluginsConfig? pluginsConfig = null)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
        _pluginsConfig = pluginsConfig ?? new PluginsConfig();
    }

    public IReadOnlyCollection<string> GetBlockedPluginIds()
    {
        lock (_gate)
        {
            return LoadStateUnsafe()
                .Where(static item => item.Disabled || item.Quarantined)
                .Select(static item => item.PluginId)
                .ToArray();
        }
    }

    public void SetRuntimeReports(
        IReadOnlyList<PluginLoadReport> reports,
        IPluginRuntimeTelemetrySource? pluginRuntimeTelemetry,
        IPluginRuntimeTelemetrySource? nativeDynamicPluginRuntimeTelemetry)
    {
        lock (_gate)
        {
            _reports = reports;
            _pluginRuntimeTelemetry = pluginRuntimeTelemetry;
            _nativeDynamicPluginRuntimeTelemetry = nativeDynamicPluginRuntimeTelemetry;
            ApplyBudgetPoliciesUnsafe();
        }
    }

    public IReadOnlyList<PluginHealthSnapshot> ListSnapshots()
    {
        lock (_gate)
        {
            ApplyBudgetPoliciesUnsafe();
            var stateById = LoadStateUnsafe().ToDictionary(static item => item.PluginId, StringComparer.Ordinal);
            var snapshots = _reports.Select(report =>
            {
                stateById.TryGetValue(report.PluginId, out var state);
                var reviewed = state?.Reviewed ?? false;
                var (trustLevel, trustReason) = DetermineTrust(report, reviewed);
                var (compatibilityStatus, errorCount, warningCount) = SummarizeDiagnostics(report.Diagnostics);
                var restartCount = GetRestartCount(report.PluginId, report.Origin);
                var memory = GetMemorySnapshot(report.PluginId, report.Origin);
                var budgetViolations = GetBudgetViolations(report, restartCount, memory);
                return new PluginHealthSnapshot
                {
                    PluginId = report.PluginId,
                    Origin = report.Origin,
                    Loaded = report.Loaded,
                    BlockedByRuntimeMode = report.BlockedByRuntimeMode,
                    Disabled = state?.Disabled ?? false,
                    Quarantined = state?.Quarantined ?? false,
                    QuarantineSource = state?.QuarantineSource,
                    Reviewed = reviewed,
                    PendingReason = state?.Reason ?? report.BlockedReason,
                    ReviewNotes = state?.ReviewNotes,
                    EffectiveRuntimeMode = report.EffectiveRuntimeMode,
                    TrustLevel = trustLevel,
                    TrustReason = trustReason,
                    CompatibilityStatus = compatibilityStatus,
                    ErrorCount = errorCount,
                    WarningCount = warningCount,
                    DeclaredSurface = BuildDeclaredSurfaceSummary(report),
                    SourcePath = report.SourcePath,
                    EntryPath = report.EntryPath,
                    RequestedCapabilities = report.RequestedCapabilities ?? [],
                    SkillDirectories = report.SkillDirectories,
                    LastError = report.Error,
                    LastActivityAtUtc = state?.UpdatedAtUtc,
                    RestartCount = restartCount,
                    WorkingSetBytes = memory?.WorkingSetBytes,
                    PrivateMemoryBytes = memory?.PrivateMemoryBytes,
                    ToolCount = report.ToolCount,
                    ChannelCount = report.ChannelCount,
                    CommandCount = report.CommandCount,
                    ProviderCount = report.ProviderCount,
                    BudgetViolations = budgetViolations,
                    Diagnostics = report.Diagnostics
                };
            }).ToList();

            foreach (var state in stateById.Values)
            {
                if (snapshots.Any(item => string.Equals(item.PluginId, state.PluginId, StringComparison.Ordinal)))
                    continue;

                snapshots.Add(new PluginHealthSnapshot
                {
                    PluginId = state.PluginId,
                    Origin = "unknown",
                    Loaded = false,
                    BlockedByRuntimeMode = false,
                    Disabled = state.Disabled,
                    Quarantined = state.Quarantined,
                    QuarantineSource = state.QuarantineSource,
                    Reviewed = state.Reviewed,
                    PendingReason = state.Reason,
                    ReviewNotes = state.ReviewNotes,
                    EffectiveRuntimeMode = null,
                    TrustLevel = state.Reviewed ? "third-party-reviewed" : "untrusted",
                    TrustReason = state.Reviewed
                        ? "Operator marked this plugin as reviewed."
                        : "Plugin has no active runtime report and has not been reviewed.",
                    CompatibilityStatus = "unknown",
                    ErrorCount = 0,
                    WarningCount = 0,
                    DeclaredSurface = "unknown",
                    SourcePath = null,
                    EntryPath = null,
                    RequestedCapabilities = [],
                    SkillDirectories = [],
                    LastError = null,
                    LastActivityAtUtc = state.UpdatedAtUtc,
                    RestartCount = 0,
                    WorkingSetBytes = null,
                    PrivateMemoryBytes = null,
                    ToolCount = 0,
                    ChannelCount = 0,
                    CommandCount = 0,
                    ProviderCount = 0,
                    BudgetViolations = [],
                    Diagnostics = []
                });
            }

            return snapshots
                .OrderBy(item => item.PluginId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public PluginOperatorState SetDisabled(string pluginId, bool disabled, string? reason)
        => UpsertState(
            pluginId,
            disabled: disabled,
            quarantined: null,
            quarantineSource: null,
            reason: disabled ? reason : string.Empty,
            reviewed: null,
            reviewNotes: null);

    public PluginOperatorState SetQuarantined(string pluginId, bool quarantined, string? reason)
        => UpsertState(
            pluginId,
            disabled: null,
            quarantined: quarantined,
            quarantineSource: quarantined ? "manual" : string.Empty,
            reason: quarantined ? reason : string.Empty,
            reviewed: null,
            reviewNotes: null);

    public PluginOperatorState SetReviewed(string pluginId, bool reviewed, string? reviewNotes)
        => UpsertState(
            pluginId,
            disabled: null,
            quarantined: null,
            quarantineSource: null,
            reason: null,
            reviewed: reviewed,
            reviewNotes: reviewed ? reviewNotes : string.Empty);

    private PluginOperatorState UpsertState(
        string pluginId,
        bool? disabled,
        bool? quarantined,
        string? quarantineSource,
        string? reason,
        bool? reviewed,
        string? reviewNotes)
    {
        lock (_gate)
        {
            var items = LoadStateUnsafe();
            var state = UpsertStateUnsafe(items, pluginId, disabled, quarantined, quarantineSource, reason, reviewed, reviewNotes);
            SaveStateUnsafe(items);
            return state;
        }
    }

    private PluginOperatorState UpsertStateUnsafe(
        List<PluginOperatorState> items,
        string pluginId,
        bool? disabled,
        bool? quarantined,
        string? quarantineSource,
        string? reason,
        bool? reviewed,
        string? reviewNotes)
    {
        var existing = items.FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
        items.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
        var state = new PluginOperatorState
        {
            PluginId = pluginId,
            Disabled = disabled ?? existing?.Disabled ?? false,
            Quarantined = quarantined ?? existing?.Quarantined ?? false,
            QuarantineSource = quarantineSource is null ? existing?.QuarantineSource : NormalizeNote(quarantineSource),
            Reviewed = reviewed ?? existing?.Reviewed ?? false,
            Reason = reason is null ? existing?.Reason : NormalizeNote(reason),
            ReviewNotes = reviewNotes is null ? existing?.ReviewNotes : NormalizeNote(reviewNotes),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        items.Add(state);
        return state;
    }

    private static bool IsManualQuarantine(PluginOperatorState? state)
        => state is { Quarantined: true } &&
           !string.Equals(state.QuarantineSource, "budget", StringComparison.OrdinalIgnoreCase);

    private void ApplyBudgetPoliciesUnsafe()
    {
        var budget = _pluginsConfig.RuntimeBudget;
        if (budget.MaxRestartCount <= 0 &&
            budget.MaxWorkingSetBytes <= 0 &&
            budget.MaxCompatibilityErrors <= 0)
        {
            return;
        }

        var items = LoadStateUnsafe();
        var changed = false;
        foreach (var report in _reports)
        {
            if (!string.Equals(report.Origin, "bridge", StringComparison.OrdinalIgnoreCase))
                continue;

            var existing = items.FirstOrDefault(item => string.Equals(item.PluginId, report.PluginId, StringComparison.Ordinal));
            if (IsManualQuarantine(existing))
                continue;

            var restartCount = GetRestartCount(report.PluginId, report.Origin);
            var memory = GetMemorySnapshot(report.PluginId, report.Origin);
            var violations = GetBudgetViolations(report, restartCount, memory);
            if (violations.Count == 0)
                continue;

            var reason = "Auto-quarantined by plugin bridge budget policy: " + string.Join("; ", violations);
            if (existing is { Quarantined: true } &&
                string.Equals(existing.QuarantineSource, "budget", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Reason, reason, StringComparison.Ordinal))
            {
                continue;
            }

            UpsertStateUnsafe(
                items,
                report.PluginId,
                disabled: null,
                quarantined: true,
                quarantineSource: "budget",
                reason: reason,
                reviewed: null,
                reviewNotes: null);
            changed = true;
            _logger.LogWarning("Auto-quarantined plugin '{PluginId}' due to bridge budget violations: {Violations}",
                report.PluginId,
                string.Join("; ", violations));
        }

        if (changed)
            SaveStateUnsafe(items);
    }

    private IReadOnlyList<string> GetBudgetViolations(
        PluginLoadReport report,
        int restartCount,
        PluginBridgeMemorySnapshot? memory)
    {
        var budget = _pluginsConfig.RuntimeBudget;
        var violations = new List<string>();
        if (budget.MaxRestartCount > 0 && restartCount > budget.MaxRestartCount)
            violations.Add($"restart count {restartCount} exceeded max {budget.MaxRestartCount}");

        if (budget.MaxWorkingSetBytes > 0 &&
            memory is not null &&
            memory.WorkingSetBytes > budget.MaxWorkingSetBytes)
        {
            violations.Add($"working set {memory.WorkingSetBytes} bytes exceeded max {budget.MaxWorkingSetBytes}");
        }

        if (budget.MaxCompatibilityErrors > 0)
        {
            var errorCount = report.Diagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
            if (errorCount > budget.MaxCompatibilityErrors)
                violations.Add($"compatibility error count {errorCount} exceeded max {budget.MaxCompatibilityErrors}");
        }

        return violations;
    }

    private int GetRestartCount(string pluginId, string origin)
    {
        var telemetry = GetRuntimeTelemetry(origin);
        if (telemetry is not null && telemetry.TryGetRestartCount(pluginId, out var restartCount))
            return restartCount;

        return 0;
    }

    private PluginBridgeMemorySnapshot? GetMemorySnapshot(string pluginId, string origin)
    {
        var telemetry = GetRuntimeTelemetry(origin);
        if (telemetry is not null && telemetry.TryGetMemorySnapshot(pluginId, out var snapshot))
            return snapshot;

        return null;
    }

    private IPluginRuntimeTelemetrySource? GetRuntimeTelemetry(string origin)
        => string.Equals(origin, "native_dynamic", StringComparison.OrdinalIgnoreCase)
            ? _nativeDynamicPluginRuntimeTelemetry
            : _pluginRuntimeTelemetry;

    private static string? NormalizeNote(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private List<PluginOperatorState> LoadStateUnsafe()
    {
        if (_cachedState is not null)
            return _cachedState;

        try
        {
            if (!File.Exists(_path))
            {
                _cachedState = [];
                return _cachedState;
            }

            var json = File.ReadAllText(_path);
            _cachedState = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListPluginOperatorState) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin state from {Path}", _path);
            _cachedState = [];
        }

        return _cachedState;
    }

    private void SaveStateUnsafe(List<PluginOperatorState> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items, CoreJsonContext.Default.ListPluginOperatorState);
            File.WriteAllText(_path, json);
            _cachedState = items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save plugin state to {Path}", _path);
        }
    }

    private static (string TrustLevel, string TrustReason) DetermineTrust(PluginLoadReport report, bool reviewed)
    {
        if (reviewed)
            return ("third-party-reviewed", "Operator marked this plugin as reviewed for deployment.");

        if (!string.Equals(report.Origin, "bridge", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(report.Origin, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ("first-party", "Plugin is loaded through a built-in or native runtime path.");
        }

        var hasStructuredSurface =
            report.RequestedCapabilities.Length > 0 ||
            report.ToolCount > 0 ||
            report.ChannelCount > 0 ||
            report.CommandCount > 0 ||
            report.ProviderCount > 0 ||
            report.SkillDirectories.Length > 0;
        var hasErrors = report.Diagnostics.Any(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));

        if (hasStructuredSurface && !hasErrors)
            return ("upstream-compatible", "Plugin declares structured capabilities and passed compatibility checks.");

        if (hasStructuredSurface)
            return ("untrusted", "Plugin declares capabilities, but compatibility checks reported blocking errors.");

        return ("untrusted", "Plugin does not expose a structured capability declaration.");
    }

    private static (string CompatibilityStatus, int ErrorCount, int WarningCount) SummarizeDiagnostics(IReadOnlyList<PluginCompatibilityDiagnostic> diagnostics)
    {
        var errorCount = diagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warningCount = diagnostics.Count - errorCount;
        var compatibilityStatus = errorCount > 0
            ? "errors"
            : warningCount > 0
                ? "warnings"
                : "verified";
        return (compatibilityStatus, errorCount, warningCount);
    }

    private static string BuildDeclaredSurfaceSummary(PluginLoadReport report)
    {
        var items = new List<string>();
        if (report.RequestedCapabilities.Length > 0)
            items.Add($"capabilities={string.Join(",", report.RequestedCapabilities)}");
        if (report.ToolCount > 0)
            items.Add($"tools={report.ToolCount}");
        if (report.ChannelCount > 0)
            items.Add($"channels={report.ChannelCount}");
        if (report.CommandCount > 0)
            items.Add($"commands={report.CommandCount}");
        if (report.ProviderCount > 0)
            items.Add($"providers={report.ProviderCount}");
        if (report.SkillDirectories.Length > 0)
            items.Add($"skills={report.SkillDirectories.Length}");

        return items.Count == 0 ? "entry-only" : string.Join(" | ", items);
    }
}
