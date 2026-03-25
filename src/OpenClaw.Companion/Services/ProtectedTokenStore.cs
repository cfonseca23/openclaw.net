using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace OpenClaw.Companion.Services;

public sealed class ProtectedTokenStore
{
    private readonly IDataProtector? _protector;
    private readonly IServiceProvider? _services;
    private readonly string _baseDir;
    private readonly string _protectedPath;
    private readonly string _fallbackPath;

    public string? LastWarning { get; private set; }

    public string ProtectedPath => _protectedPath;
    public string FallbackPath => _fallbackPath;

    public ProtectedTokenStore(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        Directory.CreateDirectory(_baseDir);

        _protectedPath = Path.Combine(_baseDir, "token.protected");
        _fallbackPath = Path.Combine(_baseDir, "token.txt");

        try
        {
            var keysDir = new DirectoryInfo(Path.Combine(_baseDir, "keys"));
            keysDir.Create();
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(keysDir)
                .SetApplicationName("OpenClaw.Companion");
            _services = services.BuildServiceProvider();
            _protector = _services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("OpenClaw.Companion.AuthToken");
        }
        catch (Exception ex)
        {
            LastWarning = $"Secure token storage unavailable; falling back to plaintext storage. {ex.Message}";
        }
    }

    public string? LoadToken(bool allowPlaintextFallback)
    {
        LastWarning = null;

        if (_protector is not null && File.Exists(_protectedPath))
        {
            try
            {
                var protectedText = File.ReadAllText(_protectedPath);
                return _protector.Unprotect(protectedText);
            }
            catch (Exception ex)
            {
                LastWarning = $"Failed to unlock protected token storage. {ex.Message}";
            }
        }

        if (!File.Exists(_fallbackPath))
            return null;

        if (!allowPlaintextFallback)
        {
            LastWarning ??= "A plaintext companion token exists, but plaintext fallback is disabled.";
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
        Directory.CreateDirectory(Path.GetDirectoryName(_protectedPath)!);

        if (_protector is not null)
        {
            try
            {
                File.WriteAllText(_protectedPath, _protector.Protect(token));
                TryDelete(_fallbackPath);
                return true;
            }
            catch (Exception ex)
            {
                warning = $"Secure token storage failed; plaintext fallback was used. {ex.Message}";
            }
        }
        else
        {
            warning = LastWarning ?? "Secure token storage unavailable.";
        }

        if (!allowPlaintextFallback)
        {
            TryDelete(_fallbackPath);
            warning = warning is null
                ? "Secure token storage is unavailable, so the token was not saved."
                : $"{warning} Token was not saved because plaintext fallback is disabled.";
            return false;
        }

        File.WriteAllText(_fallbackPath, token);
        warning = warning is null
            ? "Using plaintext companion token fallback storage."
            : $"{warning} Plaintext fallback was used.";
        return false;
    }

    public void ClearToken()
    {
        TryDelete(_protectedPath);
        TryDelete(_fallbackPath);
        LastWarning = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
