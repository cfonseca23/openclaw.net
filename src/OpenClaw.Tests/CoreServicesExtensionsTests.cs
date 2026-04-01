using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CoreServicesExtensionsTests
{
    [Fact]
    public void AddOpenClawCoreServices_RegistersLearningConfigForLearningService()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = tempPath
            }
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            IsNonLoopbackBind = false
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddOpenClawCoreServices(startup);

        using var provider = services.BuildServiceProvider();

        Assert.Same(config.Learning, provider.GetRequiredService<LearningConfig>());
        Assert.NotNull(provider.GetRequiredService<LearningService>());
    }
}
