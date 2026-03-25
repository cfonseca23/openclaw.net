using System.IO;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CliProgramTests
{
    [Fact]
    public void ResolveAuthToken_CliToken_WarnsAndTakesPrecedence()
    {
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", "env-token");

            var parsed = CliArgs.Parse(["--token", "cli-token"]);
            using var error = new StringWriter();

            var token = OpenClaw.Cli.Program.ResolveAuthToken(parsed, error);

            Assert.Equal("cli-token", token);
            Assert.Contains("--token is deprecated", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", previous);
        }
    }

    [Fact]
    public void ResolveAuthToken_EnvToken_DoesNotWarn()
    {
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", "env-token");

            var parsed = CliArgs.Parse([]);
            using var error = new StringWriter();

            var token = OpenClaw.Cli.Program.ResolveAuthToken(parsed, error);

            Assert.Equal("env-token", token);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", previous);
        }
    }
}
