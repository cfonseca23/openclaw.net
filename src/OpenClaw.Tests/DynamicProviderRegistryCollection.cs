using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DynamicProviderRegistryCollection : ICollectionFixture<object>
{
    public const string Name = "Dynamic provider registry";
}
