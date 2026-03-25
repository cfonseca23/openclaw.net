using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionSettingsStoreTests
{
    [Fact]
    public void Save_DoesNotPersistAuthTokenInSettingsJson_AndLoadsProtectedToken()
    {
        var baseDir = CreateTempDir();
        try
        {
            var store = new SettingsStore(baseDir);
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AuthToken = "top-secret"
            });

            var json = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("AuthToken", json, StringComparison.Ordinal);

            var loaded = store.Load();
            Assert.True(loaded.RememberToken);
            Assert.Equal("top-secret", loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Save_SecureStoreUnavailableWithoutOptIn_DoesNotWritePlaintextFallback()
    {
        var baseDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(baseDir, "keys"), "not-a-directory");

            var store = new SettingsStore(baseDir);
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AllowPlaintextTokenFallback = false,
                AuthToken = "top-secret"
            });

            Assert.False(File.Exists(Path.Combine(baseDir, "token.txt")));
            Assert.Contains("not saved", store.LastWarning, StringComparison.OrdinalIgnoreCase);

            var loaded = store.Load();
            Assert.True(loaded.RememberToken);
            Assert.Null(loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Save_SecureStoreUnavailableWithOptIn_WritesAndLoadsPlaintextFallback()
    {
        var baseDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(baseDir, "keys"), "not-a-directory");

            var store = new SettingsStore(baseDir);
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AllowPlaintextTokenFallback = true,
                AuthToken = "fallback-secret"
            });

            var json = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("fallback-secret", json, StringComparison.Ordinal);
            Assert.Contains("\"allowPlaintextTokenFallback\": true", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("fallback-secret", File.ReadAllText(Path.Combine(baseDir, "token.txt")));

            var loaded = store.Load();
            Assert.True(loaded.AllowPlaintextTokenFallback);
            Assert.Equal("fallback-secret", loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-companion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
