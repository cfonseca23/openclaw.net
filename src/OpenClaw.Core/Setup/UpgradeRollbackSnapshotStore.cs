using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public sealed class UpgradeRollbackSnapshotStore
{
    private const string ManifestFileName = "snapshot.json";
    private const string PayloadDirectoryName = "payload";

    private readonly string _rootPath;
    private readonly string _manifestPath;
    private readonly string _payloadPath;

    public UpgradeRollbackSnapshotStore(string configPath)
    {
        var normalizedConfigPath = Path.GetFullPath(configPath);
        var key = BuildSnapshotKey(normalizedConfigPath);
        _rootPath = Path.Combine(GatewaySetupPaths.ResolveDefaultUpgradeSnapshotRootPath(), key);
        _manifestPath = Path.Combine(_rootPath, ManifestFileName);
        _payloadPath = Path.Combine(_rootPath, PayloadDirectoryName);
    }

    public string SnapshotDirectory => _rootPath;

    public string ResolvePayloadPath(string relativePath)
        => Path.Combine(_payloadPath, relativePath);

    public UpgradeRollbackSnapshot? Load()
    {
        if (!File.Exists(_manifestPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_manifestPath), CoreJsonContext.Default.UpgradeRollbackSnapshot);
        }
        catch
        {
            return null;
        }
    }

    public bool Save(UpgradeRollbackSnapshot snapshot, Action<string> populatePayload, out string? error)
    {
        var parentDirectory = Path.GetDirectoryName(_rootPath)
            ?? throw new InvalidOperationException("Snapshot root must contain a parent directory.");
        var tempRoot = _rootPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        error = null;

        try
        {
            Directory.CreateDirectory(parentDirectory);
            var tempPayload = Path.Combine(tempRoot, PayloadDirectoryName);
            Directory.CreateDirectory(tempPayload);
            populatePayload(tempPayload);

            File.WriteAllText(
                Path.Combine(tempRoot, ManifestFileName),
                JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.UpgradeRollbackSnapshot));

            ReplaceDirectory(tempRoot, _rootPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string BuildSnapshotKey(string configPath)
    {
        var stem = Path.GetFileNameWithoutExtension(configPath);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "config";

        var safeStem = new string(stem
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safeStem))
            safeStem = "config";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configPath))).ToLowerInvariant();
        return $"{safeStem}-{hash[..12]}";
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }

        var backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(source, destination);
            Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(backup, destination);
            throw;
        }
    }
}
