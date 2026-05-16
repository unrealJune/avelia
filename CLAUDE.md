# CLAUDE.md

This file is the operating manual for Claude (and other coding agents) working in this repository. Read it before making changes. The rules here are non-negotiable; they exist because they protect the qualities the project is built around.

---

## Project context

Conductor is an open-source desktop app for managing parallel agentic coding sessions, modeled after [conductor.build](https://www.conductor.build/) but native, cross-platform-capable, and extensible. Windows is the first target.

**Architecture:**

- F# core library — domain, services, orchestration. Pure .NET 9, no platform UI types.
- C# WinUI 3 shell — presentation only. Talks to the core via typed interfaces.
- One-process model: shell and core run in the same .NET process.

**Why F# core, C# shell:** the domain is mostly state transitions, event streams, and pattern matching over message types — F#'s home turf. WinUI 3 tooling is C#-first; the shell stays where the tooling lives. The boundary is small and disciplined.

**Why no plugins yet:** the project values shipping a coherent product. Plugins (especially out-of-process plugin runtime) are a future addition, not a v1 deliverable. Contracts (interfaces) are designed with eventual plugin-ization in mind, but implementations live in-process.

---

## Non-negotiable architecture rules

These exist to keep the cross-platform door open and the inner loop fast.

1. **F# core has zero UI references.** No `Microsoft.UI.Xaml`, no `System.Windows.Forms`, no platform dispatchers. The core is a library you could host in a console app, a WinUI app, a SwiftUI app (via interop), or a test harness without modification. If you find yourself wanting to add a UI reference to the core, the answer is no — the *shell* needs the dependency, not the core.

2. **Shell never mutates core state directly.** Shell sends commands; core processes them; core emits events; shell reacts. Shell holds a projection (view-models) that mirrors core state via subscriptions. This is the discipline that makes cross-platform shells feasible later.

3. **Threading discipline.** Core methods are thread-safe and agnostic about which thread invokes them. Core never touches a UI dispatcher. Shell marshals to the UI thread (WinUI's `DispatcherQueue`) when handling subscription events. Core leaking dispatcher awareness is the moment cross-platform dies.

4. **Public boundary is C#-friendly.** F# core's public types use `IReadOnlyList<T>` not `'T list`, `Task<T>` not `Async<T>`, `IAsyncEnumerable<T>` for streams. Avoid `'T option` and `Result<'T, 'E>` in public signatures the shell touches — map them to nullable refs and a custom `OperationResult` DU with C#-friendly helper methods. Internal F# code uses the F# idioms freely.

5. **No exceptions across the core/shell boundary for expected failures.** Expected failures (auth expired, validation failed, network down) return `Result`-shaped values. Exceptions are for programmer errors and truly exceptional infrastructure failures only. The shell needs to render expected failures as UI, not crashes.

6. **Every command takes `CancellationToken`.** Every subscription is disposable. "User closed the tab mid-operation" must be clean.

7. **Storage is the source of truth for persisted state.** In-memory state is a cache. On startup, hydrate from storage. On every state change, persist. Treat the disk as the system of record.

---

## F# style guide

### Domain modeling

- **Discriminated unions for everything that has variants.** Status enums, message types, event kinds, error categories. Never strings, never int codes.
- **Records for entities.** Immutable by default. Updates produce new values (`{ task with Status = Active }`).
- **Single-case DUs for typed IDs.** `type TaskId = TaskId of Guid`, never raw `Guid`. The compiler then prevents mixing up a `TaskId` and a `ProjectId`.
- **No primitive obsession.** A "branch name" is a `BranchName`, not a `string`. A "path" is a `RepoPath` or `WorktreePath`, not a `string`. Wrap with a single-case DU and provide a smart constructor that validates.
- **Make illegal states unrepresentable.** A `Task` in `Status.Merged` can't have `MergedAt = None`. Encode this in the type: have a `MergedTask` shape that requires the merge metadata, distinct from `Task`.

Read Scott Wlaschin's *Domain Modeling Made Functional* if you haven't. It's the playbook this codebase is built on.

### Async

- **Use `task { ... }` computation expressions**, not `async { ... }`. F# `Task<T>` interops cleanly with C#; F# `Async<T>` does not.
- **`taskSeq { ... }`** for `IAsyncEnumerable<T>`. Same interop reason.
- **Don't wrap synchronous work in `task`.** Returning `Task.FromResult x` is fine.

### Error handling

- **`Result<'T, ConductorError>`** for the public-facing failure case at the boundary.
- **`ConductorError`** is a discriminated union, not a string. Each error case carries the data needed to render it.
- **Inside the F# core**, use Result freely. At the boundary, convert to the C#-friendly `OperationResult` shape.

### Naming

- **Modules and types: `PascalCase`.**
- **Functions and values: `camelCase`.**
- **Compiled names: `[<CompiledName("PascalCase")>]`** on anything publicly consumed from C#.
- **Avoid F# operators** in public APIs the shell consumes. `>>=` and friends don't carry across the boundary.

### Anti-patterns to avoid

- **Don't use `obj` or `dynamic`.** Ever. If you need a discriminated value, define a DU.
- **Don't catch and swallow exceptions.** Catch, log, return a Result.
- **Don't use mutable state in the core** except inside well-defined boundaries (e.g., a service that wraps a `Channel<T>` internally). Mutation in domain functions is a code smell.
- **Don't import C# OOP patterns wholesale.** Repository pattern is fine; "AbstractServiceFactoryBuilder" is not. F# prefers functions over interfaces when there's no implementation switching needed.
- **Don't use `Async.RunSynchronously`** or `.Result` or `.Wait()`. Block at the top of the stack, never in library code.

---

## Functional design patterns

### Modeling state machines

```fsharp
type TaskStatus =
    | Drafting
    | Active
    | Blocked of reason: string
    | InReview of prId: PullRequestId
    | Merged of mergedAt: DateTimeOffset
    | Archived
    | Abandoned of reason: string

let canTransition (from: TaskStatus) (to': TaskStatus) =
    match from, to' with
    | Drafting, Active -> true
    | Active, (Blocked _ | InReview _ | Abandoned _) -> true
    | InReview _, (Merged _ | Active) -> true
    | Merged _, Archived -> true
    | _ -> false
```

Every state transition function is total — every input has a defined output. The compiler enforces exhaustiveness.

### Event sourcing for conversations

Conversations are append-only sequences of typed events:

```fsharp
type MessageEvent =
    | UserMessageAppended of UserMessage
    | AssistantMessageAppended of AssistantMessage
    | ToolUseAppended of ToolUse
    | ToolResultAppended of ToolResult

let applyMessageEvent (conv: Conversation) (event: MessageEvent) : Conversation =
    match event with
    | UserMessageAppended m -> { conv with Messages = conv.Messages @ [User m]; LastSequence = conv.LastSequence + 1 }
    // ...
```

Replay is just `List.fold applyMessageEvent emptyConversation events`.

### Service composition

Services are interfaces (for testability) implemented by F# types:

```fsharp
type ITaskService =
    abstract CreateTaskAsync : CreateTaskRequest * CancellationToken -> Task<Result<TaskId, ConductorError>>
    abstract ObserveTask : TaskId * CancellationToken -> IAsyncEnumerable<TaskEvent>

type TaskService(persistence: IPersistence, events: IEventBus) =
    interface ITaskService with
        member _.CreateTaskAsync(req, ct) = ...
        member _.ObserveTask(id, ct) = ...
```

The shell takes `ITaskService`, not `TaskService`. Tests provide a fake.

---

## Testing requirements

### Property-based testing is the default

For every domain function that takes typed input, ask: "is there a property that should hold for any valid input?" If yes (almost always yes), write a property.

**Required PBT coverage:**

- Every state machine: transitions never produce invalid states; idempotent operations are idempotent; commutative operations commute.
- Every serializer: `deserialize (serialize x) = x` for any `x`.
- Every aggregate event-fold: replaying any prefix of events produces a valid state.
- Every ID-generating function: produces unique values across many invocations.
- Every path-handling function: never escapes its root.

**Use custom generators.** Don't rely on FsCheck's defaults for domain types — they produce nonsense. Define `Arbitrary<TaskId>`, `Arbitrary<Message>`, etc., that respect domain invariants. Generators live in `Conductor.Core.Tests/Generators.fs`.

**Shrinking matters.** When a property fails, the generator must shrink to a minimal failing case. If you see un-shrinkable failures in test output, fix the generator.

### Tier discipline

Tests are tagged with categories:
- `[<Tests>]` only — fast unit test (default)
- `Category = "Property"` — property-based, may be slow on first run
- `Category = "Integration"` — touches infrastructure (DB, filesystem, git)
- `Category = "Contract"` — runs against multiple implementations of a contract
- `Category = "E2E"` — drives the UI

The fast inner loop runs tests *without* Integration, Contract, or E2E. `./scripts/test-fast.ps1` is what you run while developing.

### When adding a feature, the test plan is:

1. Define types and write a domain function. Write 2-3 example-based tests for the obvious cases.
2. Identify the invariants. Write properties for each.
3. If it crosses persistence, add an integration test.
4. If it's part of a contract, add the contract test (it'll run against every implementation automatically).
5. If it has UI, write VM tests; for user-visible behavior changes, add an E2E test.
6. Snapshot any serialization that changed.

---

## UX testing requirements

UX testing is not "did the button click work." It is:

1. **Latency.** Streamed content (chat tokens, terminal output) renders within one frame of arrival. Test with a synthetic stream and assert on observed render time.
2. **Cancellation.** Every cancellable operation, when cancelled, leaves persisted state consistent. Test by starting an op, cancelling, then asserting on the state machine.
3. **Resumption.** Stop the app mid-task, restart, assert that conversation and worktree state are intact.
4. **Concurrency.** Two simultaneous task runs never share state or interfere. Test by spawning two tasks, sending messages, asserting isolation.
5. **Error rendering.** Every error case in `ConductorError` has a corresponding UI presentation. Drive the failure in a VM test, assert the VM exposes the right error state. Have a small E2E suite that drives common failures (network down, auth expired) and verifies the UI.
6. **Accessibility.** Every focusable UI element has `AutomationProperties.Name`. Tab order is sensible. Add a test that enumerates the accessibility tree and asserts every focusable node has a non-empty name.
7. **Visual stability.** Streaming content (chat) doesn't cause layout shift. Test with a recorded layout pre- and post-streaming.

When you add UI, you're adding all seven test surfaces. If you skip one, document why.

---

## Adding a new feature — checklist

```
[ ] Types defined (DUs and records, no primitive obsession)
[ ] State machine transitions modeled (compiler-enforced exhaustiveness)
[ ] Domain function written and pure where possible
[ ] Example-based unit tests for happy path
[ ] Property tests for invariants
[ ] Integration test if persistence is involved
[ ] Contract test updated if part of a public contract
[ ] Public API uses C#-friendly types at boundary
[ ] Cancellation supported via CancellationToken
[ ] Errors return Result<_, ConductorError>, not exceptions
[ ] Logs emitted with structured templates
[ ] Telemetry spans wrap public methods
[ ] VM tests for shell behavior
[ ] E2E test for user-visible flows
[ ] Visual regression baseline updated if appearance changed
[ ] Snapshot tests updated if serialization changed
[ ] Docs: XML doc comments on public surface
[ ] CHANGELOG entry
```

---

## Build / test / run commands

```powershell
# Bootstrap (first time only)
./scripts/bootstrap.ps1

# Build
dotnet build Conductor.sln

# Fast inner-loop tests (unit + property, no infra)
./scripts/test-fast.ps1

# All tests except E2E
./scripts/test-integration.ps1

# Everything including UI automation (slow)
./scripts/test-all.ps1

# Format everything
./scripts/format.ps1

# Run the app
cd src/Conductor.Shell.Windows
winapp run .

# Clean and re-bootstrap when in doubt
./scripts/clean.ps1
./scripts/bootstrap.ps1
```

---

## Code review checklist

Before opening a PR, verify:

- [ ] `./scripts/test-fast.ps1` passes
- [ ] `./scripts/format.ps1` is a no-op (no diff after formatting)
- [ ] No new compiler warnings
- [ ] No `obj`, `dynamic`, or stringly-typed domain values introduced
- [ ] No core changes that pulled in a UI namespace
- [ ] Any new public surface is C#-friendly
- [ ] Properties added for any new invariants
- [ ] CHANGELOG entry

The `pr-review` skill under `.github/skills/` runs a comprehensive review covering security, correctness, UX, alternative solutions, test coverage, and packaging impact. Use it before pushing.

---

## Common gotchas

1. **F# `'T list` vs `IReadOnlyList<T>` in public APIs.** F# lists are linked lists and don't bridge to C# `List<T>` semantics. Use `IReadOnlyList<T>` or `ResizeArray<T>` at the boundary.

2. **F# `Async<T>` vs `Task<T>`.** Use `task { }` (returns `Task<T>`) at boundaries, `async { }` only inside F# code that won't be called from C#.

3. **Pattern-matching on F# DUs from C#.** Works but is verbose. Provide `IsXxx` and `AsXxx` helpers on DU types intended for C# consumption, or expose a `Match` method.

4. **WinUI's DispatcherQueue.** `DispatcherQueue.GetForCurrentThread()` returns null off the UI thread. Cache the dispatcher when constructing the view-model, don't fetch it from event handlers.

5. **`IAsyncEnumerable<T>` cancellation.** Pass the `CancellationToken` to the iterator with `[EnumeratorCancellation]` attribute in C# / `cancellationToken` parameter in F# `taskSeq`.

6. **Source generators on F# projects.** Don't work. View-model source generators (`[ObservableProperty]`) are a C# / shell-project feature. The F# core uses neither.

7. **`winapp run` vs `dotnet run`.** Both work for the shell. `winapp run` is faster for iterating on packaged-app features (notifications, deep links). Use `dotnet run` for everyday code-edit-rerun cycles.

---

## What "good" looks like

Code in this repo should:

- Read like a domain spec. A non-F#-fluent reader should be able to follow the entity definitions and state transitions.
- Make refactors safe. The compiler should catch the things tests would catch on their own.
- Make tests easy. If a function is hard to test, the function is wrong.
- Make changes additive. New variants of a DU, new fields on a record, new methods on an interface — these should be the shape of growth, not breaking changes.
- Make failures obvious. A bad state should fail loudly, not silently propagate.

