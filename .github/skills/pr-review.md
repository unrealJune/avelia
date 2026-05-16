---
name: pr-review
description: Review a pull request against Avelia's non-negotiable architecture rules (F# core has zero UI refs, shell uses commands/events not direct mutation, threading discipline, C#-friendly public boundary). Use on every PR before merge.
---

# PR review skill

Walk the diff and confirm each rule below. For any violation, leave a comment with the file:line and the rule number.

## Rules to enforce

1. **F# core has zero UI references.** No `Microsoft.UI.Xaml`, no `System.Windows.*`, no platform dispatcher types in any `Avelia.Core*`, `Avelia.Persistence`, `Avelia.Vcs.*`, or `Avelia.Agent.*` project.
2. **Shell does not mutate core state directly.** Shell sends commands; core emits events. New shell code that calls private/internal core mutation methods is a violation.
3. **Public boundary is C#-friendly.** F# public types in `Avelia.Core.Abstractions` use `IReadOnlyList<T>`, `Task<T>`, `IAsyncEnumerable<T>`. `'T option` and `Result<'T, 'E>` should not appear in shell-facing signatures.
4. **Every command takes `CancellationToken`.** No fire-and-forget service methods.
5. **No exceptions for expected failures across the boundary.** Auth-expired/network-down/validation-failed return Result-shaped values, not throws.
6. **NuGet packages pinned centrally.** Any new `<PackageReference>` without a matching `<PackageVersion>` in `Directory.Packages.props` is a violation.
7. **Tests follow `<ProjectName>.Tests` naming.** A new src project without a test project is a violation unless explicitly justified in the PR.

## Output format

```
## PR review

### Blocking
- `src/.../X.fs:42` — rule 1 violation: imports `Microsoft.UI.Xaml.Controls`.

### Non-blocking
- `tests/.../Y.fs:10` — nit, missing FsCheck generator.

### Approvals
- F# core remains UI-free.
- All new public APIs take CancellationToken.
```

If nothing to flag, say so plainly.
