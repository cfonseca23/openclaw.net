using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Abstractions;
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
        try
        {
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
            Assert.NotNull(provider.GetRequiredService<ISessionAdminStore>());
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_WithSecurityServices_AllowsGatewayLlmExecutionServiceToResolveDuringValidation()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
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
            services.AddOpenClawSecurityServices(startup);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.NotNull(provider.GetRequiredService<GatewayLlmExecutionService>());
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public async Task AddOpenClawCoreServices_RegistersEmbeddingBackfillHostedService()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
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

            await using var provider = services.BuildServiceProvider();

            var backfillService = provider.GetRequiredService<SqliteEmbeddingBackfillService>();
            var hostedDescriptor = services.Last(static descriptor => descriptor.ServiceType == typeof(IHostedService));
            var hostedService = Assert.IsAssignableFrom<IHostedService>(hostedDescriptor.ImplementationFactory!(provider));

            Assert.Same(backfillService, hostedService);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
