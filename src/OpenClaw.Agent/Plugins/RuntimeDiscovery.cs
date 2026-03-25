using System.Diagnostics;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Shared utilities for discovering runtime executables (Node.js, Go, etc.).
/// </summary>
internal static class RuntimeDiscovery
{
    /// <summary>
    /// Locates a Node.js executable on the system.
    /// Checks PATH, then common installation locations (Homebrew, nvm, Windows defaults).
    /// </summary>
    public static string? FindNodeExecutable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["node.exe"]
            : ["node"];

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(candidate);

                using var proc = Process.Start(psi);
                if (proc is null) continue;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();

                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output.Split('\n', '\r')[0].Trim();
            }
            catch { }
        }

        string[] commonPaths = OperatingSystem.IsWindows()
            ? [
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\Roaming\nvm\v*\node.exe")
              ]
            : [
                "/usr/local/bin/node",
                "/usr/bin/node",
                "/opt/homebrew/bin/node",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm/versions/node/v*/bin/node")
              ];

        foreach (var path in commonPaths)
        {
            if (path.Contains('*'))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is null) continue;

                var pattern = Path.GetFileName(path);
                var parent = Path.GetDirectoryName(dir);
                var subDirPattern = Path.GetFileName(dir);

                if (parent is not null && subDirPattern is not null && Directory.Exists(parent))
                {
                    foreach (var subDir in Directory.GetDirectories(parent, subDirPattern))
                    {
                        var fullPath = Path.Combine(subDir, pattern);
                        if (File.Exists(fullPath)) return fullPath;
                    }
                }
            }
            else if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Locates an executable by name via PATH lookup (which/where).
    /// </summary>
    public static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(name);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();

            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output.Split('\n', '\r')[0].Trim();
        }
        catch { }

        return null;
    }
}
