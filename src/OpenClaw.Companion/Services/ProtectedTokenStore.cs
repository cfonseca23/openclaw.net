using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace OpenClaw.Companion.Services;

public interface ICompanionSecretStore
{
    string StorageDescription { get; }

    bool IsAvailable { get; }

    string? LoadSecret(out string? warning);

    bool SaveSecret(string secret, out string? warning);

    void ClearSecret();
}

public sealed class ProtectedTokenStore
{
    private readonly ICompanionSecretStore _secureStore;
    private readonly string _fallbackPath;

    public string? LastWarning { get; private set; }

    public string ProtectedPath => _secureStore.StorageDescription;

    public string FallbackPath => _fallbackPath;

    public ProtectedTokenStore(string? baseDir = null, ICompanionSecretStore? secureStore = null)
    {
        var resolvedBaseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        Directory.CreateDirectory(resolvedBaseDir);

        _fallbackPath = Path.Combine(resolvedBaseDir, "token.txt");
        _secureStore = secureStore ?? CompanionSecretStoreFactory.CreateDefault(resolvedBaseDir);
    }

    public string? LoadToken(bool allowPlaintextFallback)
    {
        LastWarning = null;

        if (_secureStore.IsAvailable)
        {
            var token = _secureStore.LoadSecret(out var warning);
            LastWarning = warning;
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }
        else
        {
            LastWarning = "Secure token storage is unavailable on this system.";
        }

        if (!File.Exists(_fallbackPath))
            return null;

        if (!allowPlaintextFallback)
        {
            LastWarning = LastWarning is null
                ? "A plaintext companion token exists, but plaintext fallback is disabled."
                : $"{LastWarning} Plaintext fallback is disabled.";
            return null;
        }

        LastWarning = LastWarning is null
            ? "Using plaintext companion token fallback storage."
            : $"{LastWarning} Plaintext fallback was used.";
        return File.ReadAllText(_fallbackPath);
    }

    public bool SaveToken(string token, bool allowPlaintextFallback, out string? warning)
    {
        warning = null;
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);

        if (_secureStore.IsAvailable && _secureStore.SaveSecret(token, out warning))
        {
            TryDelete(_fallbackPath);
            LastWarning = warning;
            return true;
        }

        warning ??= _secureStore.IsAvailable
            ? "Secure token storage failed."
            : "Secure token storage is unavailable on this system.";

        if (!allowPlaintextFallback)
        {
            TryDelete(_fallbackPath);
            warning = $"{warning} Token was not saved because plaintext fallback is disabled.";
            LastWarning = warning;
            return false;
        }

        File.WriteAllText(_fallbackPath, token);
        warning = $"{warning} Plaintext fallback was used.";
        LastWarning = warning;
        return false;
    }

    public void ClearToken()
    {
        _secureStore.ClearSecret();
        TryDelete(_fallbackPath);
        LastWarning = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

internal static class CompanionSecretStoreFactory
{
    public static ICompanionSecretStore CreateDefault(string baseDir)
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/security"))
            return new MacOsKeychainSecretStore(baseDir);

        if (OperatingSystem.IsWindows())
            return new WindowsDpapiSecretStore(baseDir);

        if (OperatingSystem.IsLinux() && ProcessCommandSecretStore.IsCommandAvailable("secret-tool"))
            return new LinuxSecretToolSecretStore(baseDir);

        return new UnavailableSecretStore("unavailable");
    }
}

internal sealed class MacOsKeychainSecretStore : ICompanionSecretStore
{
    private readonly string _serviceName = "OpenClaw.Companion";
    private readonly string _accountName;

    public MacOsKeychainSecretStore(string baseDir)
    {
        _accountName = BuildAccountName(baseDir);
    }

    public string StorageDescription => $"keychain:{_serviceName}/{_accountName}";

    public bool IsAvailable => File.Exists("/usr/bin/security");

    public string? LoadSecret(out string? warning)
    {
        warning = null;
        var result = ProcessCommandSecretStore.Run(
            "/usr/bin/security",
            ["find-generic-password", "-a", _accountName, "-s", _serviceName, "-w"]);
        if (result.ExitCode == 0)
            return result.StdOut.TrimEnd();

        if (!result.StdErr.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
            warning = $"Failed to load token from macOS Keychain. {result.StdErr.Trim()}";
        return null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        var result = ProcessCommandSecretStore.Run(
            "/usr/bin/security",
            ["add-generic-password", "-U", "-a", _accountName, "-s", _serviceName, "-w", secret]);
        warning = result.ExitCode == 0
            ? null
            : $"Failed to save token in macOS Keychain. {result.StdErr.Trim()}";
        return result.ExitCode == 0;
    }

    public void ClearSecret()
    {
        _ = ProcessCommandSecretStore.Run(
            "/usr/bin/security",
            ["delete-generic-password", "-a", _accountName, "-s", _serviceName]);
    }

    internal static string BuildAccountName(string baseDir)
        => "auth-token-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir))).Substring(0, 16);
}

 [SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore : ICompanionSecretStore
{
    private readonly string _ciphertextPath;

    public WindowsDpapiSecretStore(string baseDir)
    {
        _ciphertextPath = Path.Combine(baseDir, "token.dpapi");
    }

    public string StorageDescription => _ciphertextPath;

    public bool IsAvailable => true;

    public string? LoadSecret(out string? warning)
    {
        warning = null;
        if (!File.Exists(_ciphertextPath))
            return null;

        try
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var protectedBytes = File.ReadAllBytes(_ciphertextPath);
            var secretBytes = Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(secretBytes);
        }
        catch (Exception ex)
        {
            warning = $"Failed to unlock Windows protected token storage. {ex.Message}";
            return null;
        }
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        warning = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                warning = "Windows protected storage is unavailable on this system.";
                return false;
            }

            var protectedBytes = Protect(Encoding.UTF8.GetBytes(secret));
            File.WriteAllBytes(_ciphertextPath, protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            warning = $"Failed to save token in Windows protected storage. {ex.Message}";
            return false;
        }
    }

    public void ClearSecret()
    {
        try { File.Delete(_ciphertextPath); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] secret)
        => ProtectedData.Protect(secret, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] secret)
        => ProtectedData.Unprotect(secret, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

internal sealed class LinuxSecretToolSecretStore : ICompanionSecretStore
{
    private readonly string _accountName;

    public LinuxSecretToolSecretStore(string baseDir)
    {
        _accountName = MacOsKeychainSecretStore.BuildAccountName(baseDir);
    }

    public string StorageDescription => $"secret-service:openclaw-companion/{_accountName}";

    public bool IsAvailable => ProcessCommandSecretStore.IsCommandAvailable("secret-tool");

    public string? LoadSecret(out string? warning)
    {
        warning = null;
        var result = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["lookup", "service", "openclaw-companion", "account", _accountName]);
        if (result.ExitCode == 0)
            return result.StdOut.TrimEnd();

        if (!string.IsNullOrWhiteSpace(result.StdErr))
            warning = $"Failed to load token from Linux Secret Service. {result.StdErr.Trim()}";
        return null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        var result = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["store", "--label=OpenClaw Companion Auth Token", "service", "openclaw-companion", "account", _accountName],
            stdin: secret);
        warning = result.ExitCode == 0
            ? null
            : $"Failed to save token in Linux Secret Service. {result.StdErr.Trim()}";
        return result.ExitCode == 0;
    }

    public void ClearSecret()
    {
        _ = ProcessCommandSecretStore.Run(
            "secret-tool",
            ["clear", "service", "openclaw-companion", "account", _accountName]);
    }
}

internal sealed class UnavailableSecretStore : ICompanionSecretStore
{
    public UnavailableSecretStore(string storageDescription)
    {
        StorageDescription = storageDescription;
    }

    public string StorageDescription { get; }

    public bool IsAvailable => false;

    public string? LoadSecret(out string? warning)
    {
        warning = "Secure token storage is unavailable on this system.";
        return null;
    }

    public bool SaveSecret(string secret, out string? warning)
    {
        warning = "Secure token storage is unavailable on this system.";
        return false;
    }

    public void ClearSecret()
    {
    }
}

internal static class ProcessCommandSecretStore
{
    public static bool IsCommandAvailable(string command)
    {
        try
        {
            var result = Run("/usr/bin/env", ["which", command]);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            return false;
        }
    }

    public static (int ExitCode, string StdOut, string StdErr) Run(string fileName, IReadOnlyList<string> arguments, string? stdin = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
