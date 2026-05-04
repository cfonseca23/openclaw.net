# A2A

OpenClaw can expose its gateway agent through A2A when the Microsoft Agent Framework experiment is enabled. The A2A protocol is stable at v1, while the .NET SDK packages used by this integration are still preview packages.

## Enablement

A2A support is compiled only when `OPENCLAW_ENABLE_MAF_EXPERIMENT` is enabled. At runtime, set:

```json
{
  "OpenClaw": {
    "Experimental": {
      "MicrosoftAgentFramework": {
        "EnableA2A": true
      }
    }
  }
}
```

## Discovery

The standard A2A discovery endpoint is:

```text
/.well-known/agent-card.json
```

For compatibility with earlier OpenClaw experimental builds, the same card is also available under the A2A path prefix:

```text
/a2a/.well-known/agent-card.json
```

Clients should prefer the standard root discovery endpoint.

## Protocol Endpoints

By default, OpenClaw exposes these A2A protocol bindings:

| Binding | Default path |
| --- | --- |
| HTTP+JSON | `/a2a` |
| JSON-RPC | `/a2a/rpc` |

The path prefix can be changed with `OpenClaw:Experimental:MicrosoftAgentFramework:A2APathPrefix`.

## Public Base URL

Agent Card URLs are generated from the current request host by default. Set `OpenClaw:Experimental:MicrosoftAgentFramework:A2APublicBaseUrl` when the gateway runs behind a reverse proxy, container ingress, tunnel, or any host where the bind address is not externally reachable.

Example:

```json
{
  "OpenClaw": {
    "Experimental": {
      "MicrosoftAgentFramework": {
        "EnableA2A": true,
        "A2APublicBaseUrl": "https://agents.example.com/openclaw"
      }
    }
  }
}
```

With that configuration, the Agent Card advertises endpoints such as `https://agents.example.com/openclaw/a2a` and `https://agents.example.com/openclaw/a2a/rpc`.

## Authentication

Discovery is public by default so standard A2A card resolvers can fetch the Agent Card. Execution endpoints continue to use the gateway authentication and IP rate limiting policy.

For public deployments, configure gateway authentication before exposing the A2A execution paths.

## Streaming

The current A2A endpoint does not advertise protocol-level streaming. OpenClaw's internal agent runtime can stream, but the current A2A handler completes responses as a single A2A result until protocol-level streaming is implemented and tested.

## AOT and JIT Notes

This integration stays behind the Microsoft Agent Framework experiment and uses the A2A SDK preview hosting package. Core OpenClaw runtime paths remain independent of the A2A package surface.
