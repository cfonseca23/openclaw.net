# Prompt Caching

OpenClaw.NET supports prompt caching as a provider-aware optimization layered on top of the existing provider and model-profile architecture. The runtime still talks to providers through the same `ILlmExecutionService` and model-selection flow. Prompt caching only changes request shaping, normalized usage accounting, and optional keep-warm behavior.

## Why it exists

Prompt caching helps when a large prefix of the request stays stable across turns:

- base system prompt
- tool declarations
- skill prompt content
- stable workspace prompt files

When the upstream provider supports prompt caching, OpenClaw can attach cache hints and normalize returned cache usage as:

- `cacheRead`
- `cacheWrite`

This improves cost and latency visibility for long-running sessions without introducing a provider-specific runtime fork.

## Configuration

Prompt caching can be configured globally:

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4.1",
      "PromptCaching": {
        "Enabled": true,
        "Retention": "auto",
        "Dialect": "openai",
        "KeepWarmEnabled": false,
        "KeepWarmIntervalMinutes": 55,
        "TraceEnabled": false,
        "TraceFilePath": "./memory/logs/cache-trace.jsonl"
      }
    }
  }
}
```

Or per model profile:

```json
{
  "OpenClaw": {
    "Models": {
      "DefaultProfile": "gemma4-prod",
      "Profiles": [
        {
          "Id": "gemma4-prod",
          "Provider": "openai-compatible",
          "Model": "gemma-4",
          "BaseUrl": "https://gateway.example.com/v1",
          "ApiKey": "env:MODEL_PROVIDER_KEY",
          "PromptCaching": {
            "Enabled": true,
            "Dialect": "openai",
            "Retention": "auto"
          }
        },
        {
          "Id": "claude-research",
          "Provider": "anthropic",
          "Model": "claude-sonnet-4.5",
          "PromptCaching": {
            "Enabled": true,
            "Dialect": "anthropic",
            "Retention": "long",
            "KeepWarmEnabled": true,
            "KeepWarmIntervalMinutes": 55
          }
        }
      ]
    }
  }
}
```

Profile settings override the global `OpenClaw:Llm:PromptCaching` values field-by-field.

## Supported fields

- `Enabled`: turns prompt caching behavior on for that scope
- `Retention`: `none`, `short`, `long`, or `auto`
- `Dialect`: `auto`, `openai`, `anthropic`, `gemini`, or `none`
- `KeepWarmEnabled`: enables selective keep-warm for eligible providers
- `KeepWarmIntervalMinutes`: minimum warm interval
- `TraceEnabled`: emits cache-trace JSONL entries
- `TraceFilePath`: optional trace output path

## Provider behavior

### OpenAI and Azure OpenAI

- Uses deterministic cache-key hints through request additional properties
- Normalizes provider-reported cached prompt tokens into `cacheRead`
- Does not fabricate `cacheWrite` when the provider does not report it

### OpenAI-compatible

- Prompt caching is only enabled when `Dialect` is explicitly set to `openai`
- If prompt caching is enabled but the dialect stays `auto`, config validation and doctor mode warn before runtime

### Anthropic and Anthropic Vertex

- Uses Anthropic-style cache hints
- Maps provider cache read and cache creation/write usage when reported
- Eligible for keep-warm when explicitly enabled

### Amazon Bedrock

- Bedrock is available as a provider id for cache-policy routing and validation
- Anthropic-style cache behavior is only meaningful for Anthropic Claude models behind a Bedrock-compatible endpoint or adapter
- Non-Anthropic Bedrock models are treated as no-cache for retention/keep-warm purposes

### Gemini

- Uses Gemini cache dialect hints and normalized cache accounting
- Eligible for keep-warm when explicitly enabled

### Ollama

- No prompt caching behavior in v1
- Model capabilities reflect that prompt caching is unsupported

### Dynamic / plugin providers

- Prompt cache hints are passed through `ChatOptions.AdditionalProperties`
- The provider must opt into a cache dialect explicitly
- If the provider returns usage counters with cache fields, OpenClaw normalizes them into `cacheRead` / `cacheWrite`

## Diagnostics

Prompt cache usage is surfaced in:

- `/metrics/providers`
- `/doctor/text`
- session status summaries
- `/status` and `/usage` command output

If live session cache totals are missing, OpenClaw falls back to the most recent nonzero cache counters recorded in provider usage history for that session.

## Cache tracing

Cache tracing can be enabled with config:

```json
{
  "OpenClaw": {
    "Diagnostics": {
      "CacheTrace": {
        "Enabled": true,
        "FilePath": "./memory/logs/cache-trace.jsonl",
        "IncludeMessages": true,
        "IncludePrompt": true,
        "IncludeSystem": true
      }
    }
  }
}
```

Or with environment variables:

- `OPENCLAW_CACHE_TRACE=1`
- `OPENCLAW_CACHE_TRACE_FILE=/path/to/cache-trace.jsonl`
- `OPENCLAW_CACHE_TRACE_PROMPT=0|1`
- `OPENCLAW_CACHE_TRACE_SYSTEM=0|1`

Trace output is JSONL and includes:

- selected profile/provider/model
- dialect and retention
- stable fingerprint
- normalized cache usage counters

## Keep-warm

Keep-warm is intentionally conservative in v1.

- It runs in a dedicated background service
- It only warms active sessions with recent stable prompt fingerprints
- It only warms profiles that explicitly set `KeepWarmEnabled=true`
- It only applies to providers with explicit TTL or cache-resource semantics

Providers that are not explicitly eligible are skipped without failing normal requests.
