using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class StartCommandTests
{
    [Fact]
    public async Task RunAsync_ExistingConfig_LaunchesWithoutSetup()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, "{}");

            var setupCalls = 0;
            var launchCalls = 0;
            string[]? launchArgs = null;
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await StartCommand.RunAsync(
                ["--config", configPath, "--with-companion"],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false,
                new StartCommandHandlers
                {
                    ConfigExists = path => string.Equals(path, configPath, StringComparison.Ordinal),
                    RunSetupAsync = (_, _, _, _, _, _) =>
                    {
                        setupCalls++;
                        return Task.FromResult(new SetupCommandResult { ExitCode = 0, ConfigPath = configPath });
                    },
                    RunLaunchAsync = (args, _, _, _) =>
                    {
                        launchCalls++;
                        launchArgs = args;
                        return Task.FromResult(0);
                    }
                });

            Assert.Equal(0, exitCode);
            Assert.Equal(0, setupCalls);
            Assert.Equal(1, launchCalls);
            Assert.NotNull(launchArgs);
            Assert.Equal(["--config", configPath, "--with-companion"], launchArgs);
            Assert.Contains($"Using config: {configPath}", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MissingConfig_RunsSetupThenLaunch()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var setupCalls = 0;
            var launchCalls = 0;
            string[]? launchArgs = null;
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await StartCommand.RunAsync(
                [
                    "--config", configPath,
                    "--profile", "local",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--provider", "openai",
                    "--model", "gpt-4o",
                    "--api-key", "env:OPENAI_API_KEY",
                    "--open-browser"
                ],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: true,
                new StartCommandHandlers
                {
                    ConfigExists = _ => false,
                    RunSetupAsync = (_, _, _, _, _, _) =>
                    {
                        setupCalls++;
                        return Task.FromResult(new SetupCommandResult { ExitCode = 0, ConfigPath = configPath });
                    },
                    RunLaunchAsync = (args, _, _, _) =>
                    {
                        launchCalls++;
                        launchArgs = args;
                        return Task.FromResult(0);
                    }
                });

            Assert.Equal(0, exitCode);
            Assert.Equal(1, setupCalls);
            Assert.Equal(1, launchCalls);
            Assert.NotNull(launchArgs);
            Assert.Equal(
                [
                    "--config", configPath,
                    "--profile", "local",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--provider", "openai",
                    "--model", "gpt-4o",
                    "--api-key", "env:OPENAI_API_KEY",
                    "--open-browser"
                ],
                launchArgs);

            var stdout = output.ToString();
            Assert.Contains($"No config found at {configPath}. Running guided setup.", stdout, StringComparison.Ordinal);
            Assert.Contains("Setup completed. Launching gateway...", stdout, StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SetupFailure_DoesNotLaunch()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var launchCalls = 0;
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await StartCommand.RunAsync(
                ["--config", configPath, "--non-interactive"],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false,
                new StartCommandHandlers
                {
                    ConfigExists = _ => false,
                    RunSetupAsync = (_, _, _, writer, _, _) =>
                    {
                        writer.WriteLine("setup failed");
                        return Task.FromResult(new SetupCommandResult { ExitCode = 2 });
                    },
                    RunLaunchAsync = (_, _, _, _) =>
                    {
                        launchCalls++;
                        return Task.FromResult(0);
                    }
                });

            Assert.Equal(2, exitCode);
            Assert.Equal(0, launchCalls);
            Assert.Contains($"No config found at {configPath}. Running setup with the provided arguments.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("setup failed", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Setup completed. Launching gateway...", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SetupChoosesDifferentConfigPath_LaunchesUsingReturnedPath()
    {
        var root = CreateTempRoot();
        try
        {
            var requestedConfigPath = Path.Combine(root, "config", "requested.json");
            var selectedConfigPath = Path.Combine(root, "custom", "starter.json");
            string[]? launchArgs = null;
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await StartCommand.RunAsync(
                ["--config", requestedConfigPath],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: true,
                new StartCommandHandlers
                {
                    ConfigExists = _ => false,
                    RunSetupAsync = (_, _, _, _, _, _) =>
                        Task.FromResult(new SetupCommandResult { ExitCode = 0, ConfigPath = selectedConfigPath }),
                    RunLaunchAsync = (args, _, _, _) =>
                    {
                        launchArgs = args;
                        return Task.FromResult(0);
                    }
                });

            Assert.Equal(0, exitCode);
            Assert.NotNull(launchArgs);
            Assert.Equal(["--config", selectedConfigPath], launchArgs);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Help_PrintsHelpWithoutRunningHandlers()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handlerCalled = false;

        var exitCode = await StartCommand.RunAsync(
            ["--help"],
            new StringReader(string.Empty),
            output,
            error,
            Directory.GetCurrentDirectory(),
            canPrompt: true,
            new StartCommandHandlers
            {
                ConfigExists = _ =>
                {
                    handlerCalled = true;
                    return false;
                }
            });

        Assert.Equal(0, exitCode);
        Assert.False(handlerCalled);
        Assert.Contains("openclaw start", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-start-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
