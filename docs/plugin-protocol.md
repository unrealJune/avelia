# Plugin protocol (future)

Avelia v1 ships all integrations in-process. This doc captures the shape the plugin runtime will take so today's service interfaces stay compatible.

## Goals

- Plugins run **out of process**, sandboxed.
- Transport is **JSON-RPC over stdio** (no HTTP, no shared memory).
- A plugin host the same service interfaces the in-process implementations expose today.
- Plugin authors can ship in any language that can read/write stdio.

## Non-goals

- Loading plugins as in-process DLLs.
- Hot-reload.
- A marketplace.

## Service surface

The plugin protocol mirrors `Avelia.Core.Abstractions`. Every in-process service today corresponds to a future plugin verb set:

- `vcs.*` — git/forge operations (`Avelia.Vcs.GitHub` is the in-process impl).
- `agent.*` — coding-agent drivers (`Avelia.Agent.ClaudeCode` is the in-process impl).
- `persistence.*` — storage adapters.

## What lives in the repo today

Nothing for plugins yet. This file exists so contributors don't accidentally couple the core to in-process assumptions that would break a plugin runtime later. Concretely: every public service interface in `Avelia.Core.Abstractions` should be JSON-serializable.
