# MemPalace.NET memory provider

OpenClaw can use [ElBruno.MempalaceNet](https://github.com/elbruno/ElBruno.MempalaceNet) as an optional JIT-only memory backend for persistent notes and temporal knowledge graph facts.

MemPalace is not supported by the default NativeAOT gateway build. If `OpenClaw:Memory:Provider` is `mempalace`, run the gateway in JIT mode and load `OpenClaw.Plugins.Mempalace` as a dynamic native plugin.

## Enable it

Set the memory provider to `mempalace`:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "jit"
    },
    "Memory": {
      "Provider": "mempalace",
      "Mempalace": {
        "BasePath": "./memory/mempalace",
        "PalaceId": "openclaw",
        "CollectionName": "memories",
        "EmbeddingDimensions": 384,
        "KnowledgeGraphDbPath": "./memory/mempalace/kg.db",
        "SessionDbPath": "./memory/mempalace/openclaw-sessions.db"
      }
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true,
        "Load": {
          "Paths": ["./plugins/openclaw-mempalace"]
        }
      }
    }
  }
}
```

Existing `file` and `sqlite` providers remain the defaults and are unchanged.

Build or publish the MemPalace plugin into the configured plugin directory:

```bash
dotnet publish src/OpenClaw.Plugins.Mempalace -c Release -o ./plugins/openclaw-mempalace
```

Run the gateway in JIT mode with that configuration:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config <path-to-config>
```

If you publish the gateway for this lane, disable NativeAOT for that publish because dynamic native plugins are blocked in AOT mode:

```bash
dotnet publish src/OpenClaw.Gateway -c Release -p:PublishAot=false -o <output-dir>
```

The default NativeAOT gateway intentionally fails fast for this provider unless the dynamic native plugin is enabled and the effective runtime mode is JIT.

## Plugin architecture

The MemPalace integration lives in `src/OpenClaw.Plugins.Mempalace`, not in `src/OpenClaw.Gateway`. The Gateway project must not reference MemPalace packages or MemPalace types at compile time.

`OpenClaw.Plugins.Mempalace` is a dynamic native plugin assembly with `openclaw.native-plugin.json` beside the plugin DLL. Its manifest points to `OpenClaw.Plugins.Mempalace.MempalaceMemoryPlugin` and declares both `memory` and `tools` capabilities.

At startup, when `OpenClaw:Memory:Provider` is `mempalace`, Gateway creates a `NativeDynamicPluginHost` and calls `LoadMemoryProvidersAsync`. That method loads the full dynamic native plugin surface, so the same plugin instance registers both:

- `RegisterMemoryProvider("mempalace", ...)` for the `IMemoryStore` factory.
- `RegisterTool(...)` for the `mempalace_kg` tool.

Gateway stores that loaded `NativeDynamicPluginHost` on startup context and reuses it during normal plugin composition. This avoids loading the MemPalace plugin twice and keeps the registered tool sharing the same lazily-created MemPalace store as the memory provider.

`MempalaceKnowledgeGraphTool` is the single knowledge graph tool class. It can be constructed directly with an `IKnowledgeGraph` for focused tests, or with a lazy provider used by `MempalaceMemoryPlugin` so tool execution can resolve the active MemPalace store after the memory provider has been created.

## What is stored in MemPalace

- Persistent memory notes written through `memory`, `memory_get`, `memory_search`, and `project_memory`.
- Note records are stored in a MemPalace SQLite collection under a palace/collection name from configuration.
- Notes are projected into a wings / rooms / drawers hierarchy:
  - `project:demo:decision` becomes wing `project`, room `demo`, drawer `decision`.
  - Keys without enough segments use `DefaultWing` and `DefaultRoom`.
- Each saved note records temporal KG relationships:
  - `memory:<key> stored-in drawer:<drawer>`
  - `drawer:<drawer> located-in room:<room>`
  - `room:<room> located-in wing:<wing>`

Session history, branches, admin listing/search, and retention continue through OpenClaw's existing SQLite session store to preserve gateway compatibility.

## Tools

When the provider is loaded through the dynamic native plugin, OpenClaw also registers `mempalace_kg`:

- `add` writes a temporal triple using `subject`, `predicate`, and `object`.
- `query` reads triples by optional `subject`, `predicate`, `object`, and `at`.
- `timeline` lists relationships for `entity`, optionally bounded by `from` and `to`.

Entities use MemPalace's `type:id` format, for example `agent:openclaw` or `memory:project:demo:decision`.

## AOT and dependency implications

The integration is isolated in `src/OpenClaw.Plugins.Mempalace` and loaded through `INativeDynamicPlugin`. `src/OpenClaw.Gateway` does not reference the MemPalace assembly or packages at compile time. The plugin adds `MemPalace.Core`, `MemPalace.Backends.Sqlite`, and `MemPalace.KnowledgeGraph` only to the optional JIT plugin output, not to the default NativeAOT gateway build.

The adapter uses a deterministic local hashing embedder, so enabling it does not add cloud calls or API-key requirements. Treat the MemPalace provider as a JIT-only optional lane unless MemPalace itself gains validated NativeAOT support.

The plugin project sets `CopyLocalLockFileAssemblies=true` so build output includes NuGet dependency assemblies such as `MemPalace.KnowledgeGraph.dll`, `MemPalace.Core.dll`, `MemPalace.Backends.Sqlite.dll`, `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*`, and the `runtimes/` folder. Keep that setting when changing the project file; `AssemblyDependencyResolver` resolves dynamic plugin dependencies from the plugin output directory.

## Troubleshooting

If Gateway logs a failure like this during startup:

```text
Failed to load dynamic native plugin 'openclaw-mempalace-memory'
System.IO.FileNotFoundException: Could not load file or assembly 'MemPalace.KnowledgeGraph, Version=0.15.0.0'
```

then the plugin DLL was found, but one or more plugin dependency DLLs were missing from the configured plugin directory. Rebuild or publish the plugin and verify that the configured `OpenClaw:Plugins:DynamicNative:Load:Paths` directory contains `openclaw.native-plugin.json`, `OpenClaw.Plugins.Mempalace.dll`, and the `MemPalace.*.dll` dependencies.

If Gateway reports that no dynamic native memory provider registered `mempalace`, check these items:

- `OpenClaw:Runtime:Mode` is `jit`.
- `OpenClaw:Plugins:DynamicNative:Enabled` is `true`.
- `OpenClaw:Plugins:DynamicNative:Load:Paths` points to the plugin output directory, not just a source directory without built DLLs.
- `openclaw.native-plugin.json` has `assemblyPath` set to `OpenClaw.Plugins.Mempalace.dll` and `typeName` set to `OpenClaw.Plugins.Mempalace.MempalaceMemoryPlugin`.
