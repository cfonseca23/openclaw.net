using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

internal sealed class PluginAdminSettingsService
{
    private const string DirectoryName = "admin";
    private const string SettingsFileName = "plugin-admin-settings.json";
    private readonly object _gate = new();
    private readonly GatewayConfig _config;
    private readonly string _settingsPath;
    private readonly ILogger<PluginAdminSettingsService> _logger;

    public PluginAdminSettingsService(
        GatewayConfig config,
        ILogger<PluginAdminSettingsService> logger)
    {
        _config = config;
        _settingsPath = GetSettingsPath(config);
        _logger = logger;
    }

    public static string GetSettingsPath(GatewayConfig config)
    {
        var storagePath = config.Memory.StoragePath;
        if (!Path.IsPathRooted(storagePath))
            storagePath = Path.GetFullPath(storagePath);

        return Path.Combine(storagePath, DirectoryName, SettingsFileName);
    }

    public static bool TryLoadPersistedEntries(string settingsPath, out Dictionary<string, PluginEntryConfig>? entries, out string? error)
    {
        entries = null;
        error = null;

        if (!File.Exists(settingsPath))
            return false;

        try
        {
            var json = File.ReadAllText(settingsPath);
            entries = JsonSerializer.Deserialize(json, CoreJsonContext.Default.DictionaryStringPluginEntryConfig);
            if (entries is null)
            {
                error = $"Plugin admin settings file '{settingsPath}' is empty or invalid.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to load plugin admin settings from '{settingsPath}': {ex.Message}";
            return false;
        }
    }

    public static void ApplyEntries(GatewayConfig config, IReadOnlyDictionary<string, PluginEntryConfig> entries)
    {
        foreach (var (pluginId, entry) in entries)
        {
            config.Plugins.Entries[pluginId] = Clone(entry);
        }
    }

    public IReadOnlyDictionary<string, PluginEntryConfig> GetSnapshot()
    {
        lock (_gate)
        {
            return _config.Plugins.Entries.ToDictionary(
                static pair => pair.Key,
                static pair => Clone(pair.Value),
                StringComparer.Ordinal);
        }
    }

    public PluginEntryConfig? GetEntry(string pluginId)
    {
        lock (_gate)
        {
            if (!_config.Plugins.Entries.TryGetValue(pluginId, out var entry))
                return null;

            return Clone(entry);
        }
    }

    public void Upsert(string pluginId, JsonElement? config, bool? enabled = null)
    {
        lock (_gate)
        {
            if (!_config.Plugins.Entries.TryGetValue(pluginId, out var entry))
            {
                entry = new PluginEntryConfig();
                _config.Plugins.Entries[pluginId] = entry;
            }

            entry.Config = config?.Clone();
            if (enabled is bool isEnabled)
                entry.Enabled = isEnabled;

            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var snapshot = _config.Plugins.Entries.ToDictionary(
                static pair => pair.Key,
                static pair => Clone(pair.Value),
                StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.DictionaryStringPluginEntryConfig);
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist plugin admin settings to {Path}", _settingsPath);
            throw;
        }
    }

    private static PluginEntryConfig Clone(PluginEntryConfig entry)
        => new()
        {
            Enabled = entry.Enabled,
            Config = entry.Config?.Clone()
        };
}
