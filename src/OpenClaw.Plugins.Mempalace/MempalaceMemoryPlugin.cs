using OpenClaw.Core.Memory;
using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.Mempalace;

public sealed class MempalaceMemoryPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        var holder = new MempalaceMemoryProviderHolder();

        context.RegisterMemoryProvider(
            "mempalace",
            providerContext => holder.GetOrCreate(providerContext));
        context.RegisterTool(new MempalaceKnowledgeGraphTool(() =>
            holder.TryGet(out var store) && store is not null
                ? (true, store.KnowledgeGraph, null)
                : (false, null, "Error: MemPalace memory provider is not active. Set OpenClaw:Memory:Provider to mempalace.")));
    }

    private sealed class MempalaceMemoryProviderHolder
    {
        private readonly Lock _gate = new();
        private MempalaceMemoryStore? _store;

        public MempalaceMemoryStore GetOrCreate(NativeDynamicMemoryProviderContext context)
        {
            lock (_gate)
                return _store ??= new MempalaceMemoryStore(context.GatewayConfig, context.Metrics);
        }

        public bool TryGet(out MempalaceMemoryStore? store)
        {
            lock (_gate)
            {
                store = _store;
                return store is not null;
            }
        }
    }

}
