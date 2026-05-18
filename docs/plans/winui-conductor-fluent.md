# Avelia — WinUI Conductor (Fluent) Implementation Plan

## Context

The user mocked up Avelia's UI in Claude Design and exported a bundle (`WinUI Conductor - Fluent.html` + supporting CSS/JSX). The prototype shows a Conductor.build-style desktop app reimagined with Windows 11 Fluent patterns:

- TabView in the title bar (workspace tabs, File-Explorer style)
- NavigationView rail with workspace tree (icon-compact or expanded)
- 3-pane Workspace screen (pivot+chat+composer center / PR+files+terminal right)
- Settings page (side nav + cards)
- PR review with unified-diff viewer
- Inbox page
- Add Repository ContentDialog
- Light/Dark theme + 6 accent colors, Mica backdrop, Cascadia Code mono

The Avelia repo is a clean skeleton: F# core with one DU (`TaskStatus`), three near-empty service interfaces, a smoke-test C# shell (Welcome text + Greet button). All scaffolding (bootstrap, packages, test projects) is in place but no real domain or UI is implemented.

**Goal**: Build the entire Fluent shell against typed F# stub data so the design is real and exercisable, while defining a clean backend contract for a follow-up PR to wire real persistence/VCS/agent drivers.

**Decisions (locked in with user)**:

1. **Phasing**: Plan the full design, broken into reviewable chunks. Implementation happens chunk by chunk.
2. **Stub data**: Lives in F# core (`Avelia.Core/DesignData.fs`) typed against real domain — the same data path serves design-time previews and real flows once persistence lands.
3. **Fidelity**: WinUI-native controls (NavigationView, TabView, InfoBar, ContentDialog, SelectorBar, Pivot) with style tweaks to match the design — not bespoke-XAML pixel-perfect.

## Progress

| Chunk | Status | Notes |
|------:|:------:|:------|
| 0 — Theme tokens + Mica chrome | ✅ done | `Themes/{Tokens,Typography,ControlStyles}.xaml`, `Services/ThemeService.cs`, `Helpers/WindowsSystemDispatcherQueueHelper.cs`. 9 ThemeService tests. |
| 1 — F# domain + design data + stubs | ✅ done | New `Primitives.fs`, `DomainTypes.fs`, `DesignData.fs`, `Stubs.fs`, `Composition.fs`. 54 Core tests (PBT for state machines, ID uniqueness, conversation replay). |
| 2 — TabView title bar + NavView rail + Frame | ✅ done | `MainViewModel` is the composition root; 4 child VMs; 5 controls/converters; `PlaceholderPage` for not-yet-built sections. 19 shell tests including tab dedup + close-active. Sidebar mockup-parity: repos default-expanded, muted secondary-text name, count chip. |
| Post-Chunk-2 cleanup | ✅ done | Code review + targeted fixes (see "Code review fixes" below). |
| 3 — Workspace page center pane | ✅ done | `WorkspacePage` (center column real, right column stub), 6 message templates + `MessageTemplateSelector`, `WorkspaceViewModel` + 6 `MessageViewModel` subclasses, `Composer` / `ChatPivot` / `ModelBadge` / `Chip` / `CodeRefBlock` controls. F# `IConversationService.ObserveMessages` added (Channel-backed broadcast). `MessageEvent.Match` + `MessageId.Value` + `ModelChoice.Match` give C# a typed boundary so the projection never touches F# DU internals. `IUiDispatcher` (+ Immediate / DispatcherQueue impls) keeps the VM link-compileable into tests. |
| Post-Chunk-3 review fixes | ✅ done | Code-review + targeted fixes (see "Chunk-3 review fixes" below). Theme tracking fixed (dark mode regression resolved), transcript moved to virtualizing ListView, page lifecycle hardened, F# CTS leak closed, WCAG contrast + theme-usage lint tests added. 53 shell tests + 54 core tests green. |
| 4 — Workspace page right pane | ✅ done | PR header + Changes/Files selector + file list + sticky terminal panel. `PrPaneViewModel` (+`DiffFileViewModel`) loads PR + workspace diff at navigation time; `TerminalPanelViewModel` derives the prompt line from the active workspace's branch/base. `Merge` command toggles VM state on success (real API call lands in Chunk 10). File-row clicks raise `PrPaneViewModel.FileOpened` and single-select the focused row (PR-review navigation lands in Chunk 6). Observe loop moved off `Task.Run` to close a synchronous-continuation race (see "Chunk-4 fixes" below). 68 shell tests + 54 core tests green. |
| 5 — Settings page | ✅ done | Side-nav SettingsPage with 9 sections (3 real subpages — Appearance, Agents, Profile — + Placeholder for Repositories/Keyboard/Notifications/Privacy/Updates/About). F# core grew `Density` + `AccentChoice` DUs (with `Match` visitors and a static `AccentChoice.All`), `AppearanceSettings` record, and `ISettingsService` (+ `StubSettingsService` wired through `Composition`). `ThemeService.AccentChanged` now actually mutates `AveliaAccentDefaultBrush` in every merged theme dictionary at runtime (closes backlog item N-2). `SettingsCard` reusable control. `AppearanceSubpage` uses `SelectorBar` for theme/density segmented + 6-swatch accent picker (bound via `AccentSwatchItem` wrapper + `AccentChoiceToBrushConverter`); toggles bind two-way; subpage VMs guard against re-issuing setters during initial `LoadAsync`. |
| Post-Chunk-5 review fixes | ✅ done | Code-review pass against the Chunk-5 surface — bug + quality fixes in the "Chunk-5 review fixes" section below. 95 shell + 54 core tests green. |
| 6 — PR review + diff viewer | ✅ done | `PrReviewPage` (title bar with back chevron + PR# + status pill + Compare/Merge, 280 px file tree + diff viewer body, review-actions footer). `PrFileTree` is a single-select ListView reusing the `DiffFileViewModel` projection from Chunk 4. `UnifiedDiffViewer` ships header card (mono path + +/− chips + Unified/Split SelectorBar + View-file) + checks InfoBar + virtualizing hunks list; each hunk renders an accent-text header bar above an `ItemsControl` of lines. `DiffLineTemplateSelector` dispatches three context/addition/deletion templates whose backgrounds use `{ThemeResource}` so add/del row tints re-resolve on theme switch (avoids the brush-freezing bug class the `ThemeUsageLintTests` guards). `PrReviewViewModel` loads PR + files at navigation time, lazy-loads hunks for the selected file via `IDiffService.GetHunksAsync`, and computes a tri-state checks summary (Success / Warning / Error) the page mirrors onto `InfoBar.Severity`. `WorkspacePage` subscribes to `PrPane.FileOpened` and navigates the Frame to `PrReviewPage` with the file pre-selected; back chevron uses `Frame.GoBack`. Built on top of Chunk 5's merged Settings work; combined test count below. |
| 7 — Inbox page | ⏳ pending | |
| 8 — Add Repository dialog | ✅ done | `Dialogs/AddRepositoryDialog.xaml` — ContentDialog with SelectorBar across three tabs (Local folder / Git URL / Clone from GitHub). `AddRepositoryViewModel` carries per-tab state (LocalPath / GitUrl + CloneToPath / GitHubQuery + SelectedGitHubRepo) + `AddCommand` whose CanExecute keys off the active tab — local needs a valid `RepoPath.TryCreate`, Git URL needs both fields, GitHub needs a selection. `LocalPathError` mirrors the F# validator inline; the dialog renders it under the path input + disables the primary button. `IsSshUrl` is computed from the URL prefix; an `InfoBar` lights up for `git@` / `ssh://`. Browse button hands off to `Windows.Storage.Pickers.FolderPicker` (HWND-initialised via `WinRT.Interop`). `RecentRepoRow` + `RecentRepoItem` render `IRepositoryService.ListAsync` results in a virtualizing ListView; clicking a row routes through `PickRecent` (switches to Local + fills the path). `MainViewModel.OpenAddRepoDialogCommand` raises an event MainWindow turns into `ShowAsync`; `RepositoryAdded` event flows back to `MainViewModel.AppendRepository` so the new repo appears in the rail tree without re-pulling the world. Title-bar `+` button (Glyph E710) + rail "Add repository" item both trigger the command — the rail item uses `SelectsOnInvoked="False"` + a new `OnRailItemInvoked` handler so the selection doesn't move. |
| 9 — Accessibility & test pass | ⏳ pending | |
| 10 — Real backend (Persistence/VCS/Agent) | ⏳ future | Out of scope for v1 |

**Test count after Chunks 0–6 (Chunk 5 + Chunk 6 merge): 154 passing** (54 Avelia.Core, 100 Avelia.Shell.Windows — Chunk-6's PrReview VM + DiffLine/Hunk projection tests + W-3/W-5 regression guards + W-4 contrast pairings layered on Chunk-5's Settings VM coverage, plus 4 from the other F# test projects). Build is clean — 0 warnings, 0 errors.

**Test count after Chunk 8 (Add Repository dialog): 194 passing** (55 Avelia.Core — added a `RepoPath` traversal-rejection property test; 135 Avelia.Shell.Windows — added 13 VM tests for AddRepositoryViewModel + MainViewModel.AppendRepository / OpenAddRepoDialogCommand; 4 from the other F# test projects). Build is clean — 0 warnings, 0 errors.

### Chunk-8 review fixes

Ran the WinUI code-review skill against the Chunk-8 surface. Bundled fixes:

| ID | What | File(s) |
|---|---|---|
| **F-1** `[RelayCommand]` consistency | `AddRepositoryViewModel` constructed `new AsyncRelayCommand(AddAsync, CanAdd)` directly; every other VM in the codebase uses `[RelayCommand(CanExecute = nameof(...))]` on a private method (PrPaneViewModel.MergeCommand, PrReviewViewModel.MergeCommand, WorkspaceViewModel.SendMessageCommand). Switched `AddAsync` to that attribute pattern and added `[NotifyCanExecuteChangedFor(nameof(AddCommand))]` to `_activeTab` so tab switches re-query the gate. | `ViewModels/AddRepositoryViewModel.cs` |
| **F-2** Cache `LocalPathError`; declarative `HasError` | `LocalPathError` was a computed property calling `RepoPath.TryCreate` on every read, and `HasLocalPathError` called into it again — two F# validator passes per keystroke. Made it a `[ObservableProperty]` updated inside `OnLocalPathChanged`, with `[NotifyPropertyChangedFor(nameof(HasLocalPathError))]` on the field. Replaced the manual `partial void OnErrorMessageChanged(...) => OnPropertyChanged(nameof(HasError));` with `[NotifyPropertyChangedFor(nameof(HasError))]` on `_errorMessage`. | `ViewModels/AddRepositoryViewModel.cs` |
| **F-3** `RecentRepoRow` declarative bindings | The row used a code-behind `Render()` method to push values into named `TextBlock`s every time the `Item` DP changed — same anti-pattern Chunk-5 review fix Q-1 removed everywhere else. Rewrote the XAML to `{x:Bind Item.Name, Mode=OneWay, FallbackValue=''}` (mirroring `WorkspaceTreeItem`), reused the existing `AveliaChipBorderStyle` + `AveliaChipTextStyle` instead of duplicating chip styling, and bound the chip's `Visibility` to a new `RecentRepoItem.HasCount` accessor via `BoolToVisibilityConverter`. Code-behind is now just the DP declaration. | `Controls/RecentRepoRow.xaml{,.cs}`, `ViewModels/AddRepositoryViewModel.cs` |
| **F-4** Drop frozen theme snapshot | `AddRepositoryDialog` ctor copied `hostWindow.Content.ActualTheme` into `RequestedTheme` — exactly the bug class Chunk-5 E-3 removed (snapshot at construction, dialog wouldn't follow a runtime theme flip while open). Deleted the assignment; ContentDialog inherits ElementTheme through `XamlRoot` automatically. | `Dialogs/AddRepositoryDialog.xaml.cs` |
| **F-5** AutomationIds on SelectorBarItems | The three tab items had `x:Name` but no `AutomationProperties.AutomationId`, so UI tests and accessibility tools couldn't address them. Added `AddRepoLocalFolderTab` / `AddRepoGitUrlTab` / `AddRepoGitHubTab`. | `Dialogs/AddRepositoryDialog.xaml` |
| **F-6** Sizing + spacing | Root grid was hard-pinned at `Width="500"` while the dialog allows up to `ContentDialogMaxWidth=540` — the extra 40px was never used. Switched to `MinWidth="500"`. Also nudged the few off-grid spacing values (14→16, 10→12, 6→8) onto the 4px Fluent grid the rest of the codebase uses. | `Dialogs/AddRepositoryDialog.xaml` |

### Chunk-8 design notes

| Decision | Why | Where |
|---|---|---|
| **VM raises an event, MainWindow opens the dialog** | `MainViewModel` and `AddRepositoryViewModel` both link-compile into the `net10.0` test project — same constraint that drives `IUiDispatcher` (Chunk 3). Sending a `Microsoft.UI.Xaml.ContentDialog` through the VM boundary would pull WinUI into the test surface. The host catches `OpenAddRepoDialogRequested`, builds the dialog with the host's `XamlRoot`, and shows it. | `ViewModels/MainViewModel.cs`, `MainWindow.xaml.cs`, `Dialogs/AddRepositoryDialog.xaml.cs` |
| **Primary-button cancel + manual `Hide` on success** | The dialog uses `args.Cancel = true` in `OnPrimaryButtonClick` so a validation failure (e.g. `RepoPath.TryCreate` rejecting `..` traversal) keeps the dialog open with the error banner visible. On success, `AddRepositoryViewModel.RepositoryAdded` fires and the code-behind calls `Hide()`. Without this, the default close would race the error UI and the user would see the dialog vanish on a bad path. | `Dialogs/AddRepositoryDialog.xaml.cs` |
| **`RepositoryAdded` event back to the host, not a command result** | The dialog's lifecycle is "show, mutate state, close" — the host needs to know which repo to append. Bubbling a typed `Repository` through an event keeps the host's contract minimal (just subscribe + append) and avoids a second roundtrip through the service. `MainViewModel.AppendRepository` is idempotent on ID so a hypothetical double-fire can't duplicate rail entries. | `ViewModels/AddRepositoryViewModel.cs`, `ViewModels/MainViewModel.cs` |
| **`HasLocalPathError` mirror on the VM (not `StringToVisibility` converter)** | The dialog needs `Visibility` driven by "is the error string empty?". A new converter would land just for this one site; promoting the bool to a VM property is one extra line and keeps existing `BoolToVisibilityConverter` reusable. After F-2, `LocalPathError` is itself an `[ObservableProperty]` recomputed inside `OnLocalPathChanged`, and `[NotifyPropertyChangedFor(nameof(HasLocalPathError))]` on that field keeps both notifications atomic with the value change. | `ViewModels/AddRepositoryViewModel.cs`, `Dialogs/AddRepositoryDialog.xaml` |
| **Rail "Add repository" item uses `SelectsOnInvoked="False"` + `ItemInvoked`** | The rail item is an action, not a navigation target. If it stole the selection, the back-route to `Home` would require an extra round-trip through `NavigateToSection`. Routing through `ItemInvoked` (new handler) keeps `SelectionChanged` untouched while still raising the command on click + keyboard activate. | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| **Browse uses `Windows.Storage.Pickers.FolderPicker` + HWND init** | WinUI 3 unpackaged hosts need the parent HWND threaded through `WinRT.Interop.InitializeWithWindow` before `PickSingleFolderAsync` will render. The dialog stores the host window in its ctor and threads the handle in on click; failures (sandbox, locked-down deployment) log and let the user type the path manually. | `Dialogs/AddRepositoryDialog.xaml.cs` |

### Chunk-6 review fixes

Ran the WinUI code-review skill against the Chunk-6 surface. Bundled fixes:

| ID | What | File(s) |
|---|---|---|
| **W-1** Nullable row `OpenCommand` | `PrReviewViewModel` constructed rows with a no-op `Action<RelativePath>` because PR Review routes clicks through `PrFileTree.FileSelected` instead of the row's command. The no-op silently swallowed any future invocation of `OpenCommand` from a hypothetical future caller. Made `DiffFileViewModel.onOpen` nullable; when null, `OpenCommand` is null and binding sites no-op safely. `FileChangeList`'s click handler now null-checks the command. While there, also fixed `AddDisplay`/`DelDisplay` to return empty for zero (was `"+0"` / `"-0"`). | `ViewModels/DiffFileViewModel.cs`, `Controls/FileChangeList.xaml.cs`, `ViewModels/PrReviewViewModel.cs`, test sites |
| **W-2** Disable dead UI affordances | The Compare button, the three review-footer buttons (Request changes / Comment / Approve), the View-file button, and the Split selector item rendered fully styled but had no behavior — clicking Approve gave the user no feedback. Set `IsEnabled="False"` + `ToolTipService.ToolTip` explaining "ships with the GitHub adapter" / "needs the VCS adapter". The selector handler also bounces selections to disabled items back to Unified as defence-in-depth. Compare's glyph swapped from `&#xE8AB;` (Merge) to `&#xE1D9;` (ImportAll). | `Pages/PrReviewPage.xaml`, `Controls/UnifiedDiffViewer.xaml{,.cs}` |
| **W-3** InfoBar "Success" flash before load | `InfoBar.IsOpen` was bound to `HasPullRequest`, so it opened with the XAML-default `Severity="Success"` between page paint and `LoadAsync` populating check counters. With stubs that's invisible; with any network latency you'd see a green "All checks passed" flash followed by yellow/red. Added `ShowChecksInfoBar` (`HasPullRequest AND ChecksTotal > 0`) on the VM; bound `IsOpen` to it. Test added. | `ViewModels/PrReviewViewModel.cs`, `Controls/UnifiedDiffViewer.xaml` |
| **W-4** New diff brush pairings in `ThemeContrastTests` | The diff templates introduced two new fg/bg pairs (`TextSecondary` on `DiffAddStrong`/`DiffDelStrong`, `AccentText` on `SubtleFillTertiary`) that `ThemeContrastTests`' table didn't cover — tests passed only because they didn't look. Added four pairings; all pass WCAG AA in both themes (large-text floor for the strong-tint line numbers since they're compact labels). | `tests/Avelia.Shell.Windows.Tests/ThemeContrastTests.cs` |
| **W-5** CTS guard on `LoadAsync` | `PrReviewViewModel.LoadAsync` and `PrPaneViewModel.LoadAsync` had no in-flight guard — two near-simultaneous calls (fast renavigation, or a future "refresh") could interleave: the second call's `Files.Clear()` could land mid-way through the first's fill, tearing observable state. Each VM now holds a `_loadCts`; a new call cancels the previous via `CancellationTokenSource.CreateLinkedTokenSource(ct)`, and `OperationCanceledException` is swallowed so the superseded call exits silently. Regression test asserts last-load-wins under contention. | `ViewModels/PrReviewViewModel.cs`, `ViewModels/PrPaneViewModel.cs`, tests |

### Chunk-6 design notes

| Decision | Why | Where |
|---|---|---|
| **Three diff-line `DataTemplate`s** (context / addition / deletion) selected by `DiffLineTemplateSelector` rather than a single templated row resolving brushes in code-behind. | The first draft was a `DiffLineRow` UserControl that resolved `AveliaDiff*Brush` via `Application.Current.Resources` — same shape as the Chunk-3 dark-mode bug. `ThemeUsageLintTests` would not have caught it (it lints XAML, not C#), but `ActualThemeChanged` would have needed plumbing on every row, and a hundreds-of-lines diff means hundreds of subscriptions. The template-per-kind pattern keeps all brushes declarative `{ThemeResource}`, costs ~50 lines of XAML, and shares the virtualizing ListView's container realization. | `Controls/UnifiedDiffViewer.xaml`, `Controls/DiffLineTemplateSelector.cs` |
| **`ChecksSeverityName` as `string`, not `InfoBarSeverity`** | Keeping the VM clear of `Microsoft.UI.Xaml.Controls` is what lets it link-compile into the `net10.0` test project (same constraint as `IUiDispatcher` from Chunk 3). `UnifiedDiffViewer` mirrors the string onto `InfoBar.Severity` in its `PropertyChanged` handler. | `ViewModels/PrReviewViewModel.cs`, `Controls/UnifiedDiffViewer.xaml.cs` |
| **Frame-based navigation, not a router** | `WorkspacePage` subscribes to `PrPane.FileOpened` and calls `Frame.Navigate` directly. `MainWindow` doesn't own this — keeping navigation a local page concern means back-nav is just `Frame.GoBack()` and there's no shell-wide state to keep in sync. | `Pages/WorkspacePage.xaml.cs`, `Pages/PrReviewPage.xaml.cs` |
| **`RelativePath?` on `PrReviewPageArgs`** | `RelativePath` is a `[<Struct>]` F# value type. C# 8+ nullable-struct syntax (`RelativePath?` → `Nullable<RelativePath>`) lets the VM disambiguate "preselect this file" from "fall back to the first". `Folder` / `FileName` accessors still come from the F# `RelativePath` type so the C# side stays free of path-parsing. | `Pages/PrReviewPage.xaml.cs`, `ViewModels/PrReviewViewModel.cs` |
| **Hunks via `ItemsControl`, hunks list via `ListView`** | Hunks are short (≤30 lines typical); virtualizing them inside a virtualizing outer is fragile and gives back nothing. Outer ListView virtualizes hunks → cheap per file. Inner ItemsControl realizes lines eagerly → no measure-to-infinity blowup because the outer is laid out by ListView's stack panel. | `Controls/UnifiedDiffViewer.xaml` |

### Chunk-4 fixes

| ID | What | File(s) |
|---|---|---|
| **F-1** Observe-loop race | `StartObserving` ran the `await foreach` inside `Task.Run`. The F# stub channel uses `AllowSynchronousContinuations = true`, which means a write only fires the consumer's continuation synchronously if the consumer is already at `MoveNextAsync` when the write happens. With `Task.Run`, scheduling latency could land an event in the channel queue before the consumer reached its first await — the test then asserted on `Messages.Count` before the loop had drained it. Moved the loop inline (it yields at every `MoveNextAsync`, so it doesn't block the UI thread); registration on the F# side is synchronous, so by the time `LoadAsync` returns the channel is registered AND the consumer is awaiting. Race observed and reproduced 10/10 with the new test load; 0/10 after the fix. A real backend with blocking I/O can re-introduce `Task.Run` on its side of the boundary (per CLAUDE.md threading discipline §3). | `ViewModels/WorkspaceViewModel.cs` |

### Chunk-5 review fixes

Ran a code-review pass against the Chunk-5 surface. Bundled fixes:

| ID | What | File(s) |
|---|---|---|
| **E-1 / E-2** Accent brush mutation was a no-op | `MainWindow.ApplyAccent` walked `Application.Current.Resources.ThemeDictionaries` directly, but `Tokens.xaml` is a *merged* dictionary, so the theme dicts live under `MergedDictionaries[*].ThemeDictionaries`. The lookup found nothing and the picker silently did nothing. New `Helpers/ThemeResources.EnumerateThemeDictionaries()` walks the right tree; `ApplyAccent` iterates it. | `MainWindow.xaml.cs`, `Helpers/ThemeResources.cs` |
| **E-3** Stale theme resolution in code-behind | `Application.Current.Resources["AveliaAccentDefaultBrush"]` resolves against `Application.RequestedTheme` (frozen at startup), so the side-nav accent bar + accent swatch ring used the wrong color after a runtime theme flip. Both call sites are gone — replaced by `x:Bind` to `{ThemeResource}`-backed XAML (`SettingsSectionItem.IsActive` → bar Visibility; `AccentSwatchItem.IsSelected` → ring Visibility). Framework owns theme tracking. | `Pages/SettingsPage.xaml{,.cs}`, `Pages/SettingsSubpages/AppearanceSubpage.xaml{,.cs}`, `ViewModels/SettingsViewModel.cs`, `ViewModels/AppearanceSubpageViewModel.cs` |
| **E-4** `ThemeService.ThemeChanged` event leak | `AppearanceSubpageViewModel` subscribed in its ctor and never unsubscribed — every Settings open leaked a VM. Made the VM `IDisposable`; `SettingsViewModel.Dispose` cascades; `SettingsPage.OnNavigatedFrom` invokes it. | `ViewModels/AppearanceSubpageViewModel.cs`, `ViewModels/SettingsViewModel.cs`, `Pages/SettingsPage.xaml.cs` |
| **E-5** Silent fire-and-forget on settings writes | Bare `_ = _settings.SetXxxAsync(...)` calls would swallow service failures with no log, no UI, nothing. Added a `FireAndForget(task, op)` helper in each subpage VM that logs via `Debug.WriteLine` on faulted-task continuation. Settings page's `LoadSafelyAsync` wraps `LoadAsync` the same way (mirrors the `InitializeViewModelSafelyAsync` pattern in `MainWindow`). | `ViewModels/AppearanceSubpageViewModel.cs`, `ViewModels/AgentsSubpageViewModel.cs`, `Pages/SettingsPage.xaml.cs` |
| **E-6** Two incompatible hex parsers | One in `MainWindow.xaml.cs` treated 8-char as `AARRGGBB`, the other in `AppearanceSubpage.xaml.cs` treated 8-char as `RRGGBBAA`. `AccentChoice.Hex` only emits 7-char so no live bug today, but any future 8-char hex would silently render different colors depending on the call path. Extracted single `Helpers/HexColor.TryParse` — `#AARRGGBB` per Tokens.xaml header comment. Both old parsers deleted. | `Helpers/HexColor.cs`, `Converters/AccentChoiceToBrushConverter.cs`, `MainWindow.xaml.cs`, `Pages/SettingsSubpages/AppearanceSubpage.xaml.cs` |
| **Q-1** Visual-tree walks replaced with x:Bind through wrapper items | The original SettingsPage / AppearanceSubpage / AgentsSubpage each walked the visual tree to mutate Buttons / Ellipses / RadioButtons that the framework should have been driving. Three wrapper item types (`SettingsSectionItem`, `AccentSwatchItem`, `AgentModelOption`) gained an observable `IsActive`/`IsSelected`; their DataTemplates bind `Visibility`/`IsChecked` via `x:Bind`. Three `FindXxxByYyy` helpers + two `_suppressXxxEvents` flags gone. | `ViewModels/SettingsViewModel.cs`, `ViewModels/AppearanceSubpageViewModel.cs`, `ViewModels/AgentsSubpageViewModel.cs`, `Pages/SettingsPage.xaml{,.cs}`, `Pages/SettingsSubpages/AppearanceSubpage.xaml{,.cs}`, `Pages/SettingsSubpages/AgentsSubpage.xaml{,.cs}`, `Converters/BoolToVisibilityConverter.cs` |
| **Q-2** Stale subpage cache | `SettingsPage` cached materialized subpages but re-navigation built a fresh `SettingsViewModel` — cached subpages kept their old (now-disposed) VM references. Dropped the cache (subpage UserControls are cheap to construct). | `Pages/SettingsPage.xaml.cs` |
| **Q-3** `BackAction` double-navigated | `MainWindow.NavigateToSettings`'s back closure called both `NavigateSectionCommand` and set `RailNav.SelectedItem` — the latter's `SelectionChanged` handler then ran the command + frame nav again. Reduced to a single `RailNav.SelectedItem = HomeItem` assignment; SelectionChanged handler is the single source of truth. | `MainWindow.xaml.cs` |
| **Q-4** Shared `ThemeResources.Resolve` helper | The "walk MergedDictionaries → ThemeDictionaries by ActualTheme" pattern existed in `CodeRefBlock` already; extracted to `Helpers/ThemeResources.Resolve(host, key)` for any future caller that needs theme-keyed runtime lookup. `CodeRefBlock` migrated to use it. | `Helpers/ThemeResources.cs`, `Controls/CodeRefBlock.cs` |

### Chunk-5 notes

| Area | Decision / rationale |
|---|---|
| **Theme stays shell-side, every other appearance pref lives in F#** | `ISettingsService` covers Accent + Density + Transparency + OpenWithRightPanel + DefaultModel + ExtendedThinking. Theme (Light/Dark/System) stays on `ThemeService` because it touches WinUI `ElementTheme` — that's platform state, not domain state. Per the original plan deviation #2 from Chunk 2. |
| **`AccentChoice.All` is a static member, not a module function** | F# `module AccentChoice = ...` after `type AccentChoice` auto-suffixes to `AccentChoiceModule` in C# IL, which is awkward to consume. Static `AccentChoice.All` reads as `AccentChoice.All` from both languages. |
| **Settings VM stays platform-free; the page does the WinUI work** | `SettingsViewModel` / `AppearanceSubpageViewModel` / `AgentsSubpageViewModel` / `ProfileSubpageViewModel` all link-compile into the net10.0 test project. The `Pages/SettingsSubpages/*.xaml.cs` code-behind handles SelectorBar selection translation, accent-swatch visual tree walks, and the side-nav active-row accent bar — none of that is testable headless, but the data flow underneath it is. |
| **`AccentChanged` actually does something now (closes backlog N-2)** | New `MainWindow.ApplyAccent` parses the hex once and writes `AveliaAccentDefaultBrush` into both Light and Default theme dictionaries. New brush instance per dictionary (mutating one in-place doesn't repaint the other when the theme flips). Subscribed in MainWindow ctor; unsubscribed in `OnClosed`. |
| **`partial void OnXxxChanged` + `_isLoading` guard** | `[ObservableProperty]` emits `OnXxxChanged` partial-method hooks. The Appearance/Agents subpage VMs use them to push setters through to `ISettingsService` on user gesture — but `LoadAsync` would re-trigger them with the value we just read from the service. `_isLoading` short-circuits during the initial hydrate. |
| **SettingsCard's `Action` slot, not the default Content** | `UserControl.Content` is already consumed by the inner Grid. `SettingsCard` exposes a separate `Action` DP that consumers fill via `<controls:SettingsCard.Action>...</controls:SettingsCard.Action>` — explicit element-syntax, no ContentProperty attribute games. |
| **Three real subpages, six placeholder ones** | Appearance, Agents, Profile have content. Repositories / Keyboard / Notifications / Privacy / Updates / About route to `PlaceholderSubpage` with the section name — matches the design (which doesn't render these subpages in the prototype either). |
| **Back chevron returns to Home, not browser-style "previous page"** | `SettingsPage` takes a `BackAction` in its nav args; `MainWindow.NavigateToSettings` passes a closure that navigates back to the active workspace and re-selects the Home rail item. Simpler than a Frame back-stack and matches the design's left-chevron-as-page-close affordance. |

### Chunk-3 review fixes

Ran a code-review pass against the Chunk-3 surface. Bundled fixes:

| ID | What | File(s) |
|---|---|---|
| **E-1** Dark-mode brush capture | Resolved brushes via `Application.Current.Resources["X"]` in C# freeze at first paint and don't track theme — workspace name stayed black on dark theme, code-style refs stayed dark-blue. Added `AveliaRepoGroupNameStyle` / `AveliaRepoGroupCountStyle` (TextBlock styles holding `{ThemeResource ...}`); `BuildRepoNavItem` now applies the styles. `CodeRefBlock` subscribes to `ActualThemeChanged` and re-resolves the accent brush from the merged ThemeDictionaries each rebuild. `Chip` moved its defaults to XAML `{ThemeResource}` initial values; DP change handlers only override when consumers explicitly supply a brush. | `Themes/ControlStyles.xaml`, `MainWindow.xaml.cs`, `Controls/CodeRefBlock.cs`, `Controls/Chip.xaml{,.cs}` |
| **E-2** Transcript virtualization | `ItemsRepeater` inside a `ScrollViewer` realizes every row up front (the ScrollViewer's measure-to-infinity defeats virtualization). Replaced with `ListView` (built-in UI virtualization) + `AveliaTranscriptItemContainerStyle` stripping selection/hover chrome. | `Pages/WorkspacePage.xaml`, `Themes/ControlStyles.xaml` |
| **E-3 / W-1** Page lifecycle | `OnNavigatedFrom` was `async void` racing itself, and the VM was rebuilt + bindings refreshed on every navigation. Fixed: page constructs the VM once, subsequent navigations call `LoadAsync` against a new workspace id (which already handles "swap workspace" cleanly); `OnNavigatedFrom` is synchronous and calls a new `WorkspaceViewModel.StopObserving()` that cancels the CTS without awaiting. | `Pages/WorkspacePage.xaml.cs`, `ViewModels/WorkspaceViewModel.cs` |
| **W-3** Composer Enter | Old code unconditionally marked Enter as handled, swallowing the keystroke even when the composer was empty (user typed Enter into a blank box and nothing visible happened). Now only handled if `CanExecute` succeeds. | `Controls/Composer.xaml.cs` |
| **W-5** Drop unused `ThreadSelected` event | The `TwoWay` binding on `ActiveThread` was the actual signal; the event had no consumers. | `Controls/ChatPivot.xaml.cs` |
| **W-7** `MessageViewModel` is a DTO, not observable | Subclasses had no observable properties; the inherited `ObservableObject` overhead was pure cost. Dropped the base. Streaming text will be a sibling VM, not a mutation. | `ViewModels/MessageViewModel.cs` |
| **W-8** Observe loop only caught OCE | Real-backend errors would crash the process via unobserved task. Now catches `Exception` and logs to Debug. | `ViewModels/WorkspaceViewModel.cs` |
| **W-10** F# `CancellationTokenRegistration` leak | The registration captured the channel + subscribers list for the lifetime of the CT, even after the channel completed. Now disposed on `Reader.Completion`. | `Avelia.Core/Stubs.fs` |
| **N-1** Drop `Mode=OneWay` on the SendCommand binding | Command bindings don't change; OneTime is correct. | `Pages/WorkspacePage.xaml` |
| **N-9** Wire Composer `ModelName` from VM | Was a dead "Sonnet 4.5" literal. Added `WorkspaceViewModel.ModelName` populated from the workspace's `Agent` via a new `ModelChoice.Match` visitor. | `ViewModels/WorkspaceViewModel.cs`, `Pages/WorkspacePage.xaml`, `Avelia.Core.Abstractions/DomainTypes.fs` |
| **A11y tests** | New `ThemeContrastTests` parses `Tokens.xaml` and asserts WCAG 2.1 AA contrast (4.5:1 body, 3:1 large) for every text/surface pairing in both Light and Default theme dictionaries, with alpha compositing onto Mica base. New `ThemeUsageLintTests` scans every shell XAML for hardcoded hex literals on color attributes, and for `{StaticResource}` references to `Avelia*Brush` keys (which would freeze the brush at parse time) — catches the E-1 bug class at source level. **A runtime A11y scan via `Axe.Windows.Automation` is the next-tier test and belongs in Chunk 9**, where it'll run against the live shell. | `tests/Avelia.Shell.Windows.Tests/ThemeContrastTests.cs`, `ThemeUsageLintTests.cs` |

### Code review fixes (post-Chunk-2)

Ran the WinUI code-review skill against the Chunks 0–2 surface. Landed the high-priority bugs:

| Fix | File(s) | What changed |
|---|---|---|
| **W-3** Bindings hardened against null DP chains | `Controls/WorkspaceTabHeader.xaml`, `Controls/WorkspaceTreeItem.xaml` | Added `FallbackValue` to every `x:Bind` through the `Tab.X` / `Item.X` paths (`{x:Null}` for ref types, `''` for strings, `Collapsed` for Visibility). Prevents flashes / crashes if the parent DP isn't yet assigned. |
| **W-4** Rail pane re-entry guard | `MainWindow.xaml.cs` | `_suppressRailEvents` flag wraps `ApplyRailDisplayMode` so the programmatic `PaneDisplayMode` mutation can't ping-pong with the VM via `PaneOpening`/`PaneClosing` handlers. |
| **W-7** Single Mica configuration | `MainWindow.xaml` | Removed duplicate `<Window.SystemBackdrop><MicaBackdrop/>` — `TrySetSystemBackdrop` in code-behind is now the sole source, with the `DesktopAcrylic` fallback intact. |
| **W-9** Safe fire-and-forget on init | `MainWindow.xaml.cs` | `_ = InitializeViewModelSafelyAsync()` wraps `ViewModel.InitializeAsync` in a try/catch + `Debug.WriteLine`. Real backend (Chunk 10) will throw; we no longer swallow it as an unobserved task. |
| **W-10** Quiet the MVVMTK0045 noise | `Avelia.Shell.Windows.csproj` | `<NoWarn>$(NoWarn);MVVMTK0045</NoWarn>` with a comment pointing to when to drop it (when CommunityToolkit.Mvvm ships the partial-property generator). |

### Backlog from the code review (not yet done)

These are queued for Chunk 9's accessibility & polish pass — none are bugs:

- **W-2** Rename `MainViewModel.OpenWorkspace` → `OpenWorkspaceAsync` (and update test/handler call sites).
- **W-5** `StatusDot` should carry `AutomationProperties.Name` bound through `WorkspaceStatusToLabelConverter` so screen readers announce the status without hover.
- **W-6** Theme toggle button name should reflect current state ("Switch to light theme" / "Switch to dark theme" via converter).
- **W-8** Move the merge button's hardcoded `#FFFFFF` / `#1AFFFFFF` into `AveliaMergeButtonForegroundBrush` / `AveliaMergeButtonBorderBrush` tokens in `Tokens.xaml`.
- **N-1** Decide whether `MainViewModel`'s parameterless constructor stays as design-time-only or is removed.
- ~~**N-2** Wire `ThemeService.AccentChanged` to actually swap `AveliaAccentDefaultBrush`~~ — done in Chunk 5 (`MainWindow.ApplyAccent`).
- **N-3** Replace `string`-tag round-trip parsing in `OnRailSelectionChanged` with a typed lookup.
- **N-6 / N-7** Consolidate hardcoded `FontSize` / `Width` / `Height` literals into named styles.
- **N-8** Audit unused styles in `ControlStyles.xaml` (`AveliaIconSmallButtonStyle`, `AveliaCaptionButtonStyle`, `AveliaSectionLabelTextStyle`, `AveliaAccentButtonStyle`) and either reference or remove.
- **N-10** Cache resolved brushes in `WorkspaceStatusToBrushConverter` (one-time lookup instead of dictionary access per call).
- **N-12** Migrate hardcoded user-visible strings to `x:Uid` + `Resources.resw` (Chunk 9 territory).
- **N-13** `DesignDataTests.fs` — replace tautological `Assert.NotNull` checks with calls that actually exercise the services.
- **N-14 / N-15** F# diagnostic ergonomics (short ID `Display`, `TryGetValue(out)` for `OperationResult`).
- **N-17** Document the test-project link-compile pattern in `AGENTS.md` so new VM files don't quietly skip test coverage.

### Deviations from the original plan (worth tracking)

1. **`[ObservableProperty]` uses the field-based pattern, not partial properties.** MVVM Toolkit 8.4 emits `MVVMTK0045` warning recommending partial properties for AOT/CsWinRT, but the generator hasn't shipped support for the partial-property feature in this version (declarations produce `CS9248 Partial property must have implementation`). Warning is now suppressed via `NoWarn`; migrate when the toolkit version bumps.
2. **`IThemeService` / `ISettingsService` stayed C#-only.** The plan put them in F# core; in practice they're shell-internal state with WinUI side effects, so a single shell-side `ThemeService` is enough. Revisit if persistence needs to read them.
3. **`Observe*` (live-streaming) service methods landed in Chunk 3.** `IConversationService.ObserveMessages` returns `IAsyncEnumerable<MessageEvent>` backed by a per-subscriber `Channel<MessageEvent>` in `StubConversationService`. `AllowSynchronousContinuations = true` on the channel keeps stub-driven flows observable on a single thread (the test pattern). Real-backend builds should leave the option off so slow consumers can't stall the writer.

3a. **F#/C# DU boundary.** Pattern-matching on F# DU nested case classes from C# is technically possible but leaks generated `Item` accessors into the shell. Following the precedent set by `OperationResult.Match`, `MessageEvent` now exposes a typed `Match<'TResult>` member taking one `Func` per case — C# calls `ev.Match(onUser: …, onAgent: …, …)` and never touches the union internals. Adding a new event kind forces a compile error in `MessageViewModel.FromEvent`. `MessageId` likewise grew a `.Value` member so `MessageId.value(id)` (an F# module function with the awkward `MessageIdModule.value` C# name) isn't needed.

3b. **`IUiDispatcher` abstraction.** WinUI's `DispatcherQueue` is in `Microsoft.UI.Dispatching`, which would prevent `WorkspaceViewModel` from link-compiling into the net10.0 test project. Introduced `IUiDispatcher` (interface-only, link-compileable) with `DispatcherQueueUiDispatcher` (production, WinUI ref) and `ImmediateUiDispatcher` (synchronous, tests). The VM captures the dispatcher at construction per CLAUDE.md §gotcha-4.
4. **No `BoolToRailDisplayMode` converter.** Using `{StaticResource Converter}` inside an `{x:Bind}` on a `Window` makes the XAML codegen try to cast `this` (Window) to `FrameworkElement`, which fails. Bound `NavigationView.PaneDisplayMode` from code-behind on `PropertyChanged` instead. Bonus: keeps `Microsoft.UI.Xaml.*` out of the VM so it still link-compiles into the `net10.0` test project.

### Where to pick up next

Chunk 7 — Inbox page (skipped over for Chunk 8; Chunk 8 is now done). Goal: page header + scrollable card of inbox items, each row a leading kind icon (warn/success/info) + title/desc + time + chevron. Click with `LinkedWorkspaceId` set raises a `WorkspaceOpenRequested` event the shell turns into `OpenWorkspace`. `IInboxService.ListAsync` already returns the seeded items; new wiring is purely shell-side. See "Chunk 7" section below.

Notes left behind from Chunk 8:
- `AddRepositoryDialog`'s GitHub tab renders the empty-state hint until the VCS adapter ships (Chunk 10). `AddCommand` is disabled while `SelectedGitHubRepo` is null, so the button doesn't fire there even if the user tabs to that surface.
- Git URL tab calls the same `IRepositoryService.AddAsync` as Local Folder, using `CloneToPath` as the destination — the actual clone is a no-op today. Real clone hand-off lands with the VCS adapter (Chunk 10); the dialog's tab shape and validation are already in place to accept it.
- `RecentRepoItem.WorkspaceCount` is wired in the row template but the stub doesn't surface per-repo workspace counts on the `Repository` record. Empty string hides the chip; populate when the persistence layer can produce it cheaply.

Notes left behind from Chunk 5 + Chunk 6 follow-up:
- The `Changes` / `Files` SelectorBar in `WorkspacePage.xaml` is still visual-only (both pivots render the same diff). Chunk 10's backend split between workspace diff and PR diff is the trigger to wire `SelectionChanged` to `IDiffService.GetPullRequestDiffAsync`. Same shape applies to PR Review's Unified / Split selector — the second view ships as an affordance for a future side-by-side renderer.
- `PrReviewPage`'s Compare / Request changes / Comment / Approve buttons render but have no command bindings — these are PR review affordances waiting on Chunk 10's PR / VCS adapters.
- `View file` button in the diff header is unwired (planned to launch the local editor / web view of the file at the current revision — needs the VCS adapter).
- The seeded `RepositoryDetailsDialog.tsx` hunks have a couple of duplicated line numbers on the addition side (e.g. `44, Addition` repeated). That's a property of the design fixture, not a renderer bug; real `git diff` output won't have it. If a future visual-regression baseline trips on it, regenerate the seed.
- Settings backlog: density/transparency toggles fire setters but no consumer applies them visually yet. Density is a no-op until row paddings / chat spacing are conditionally bound; Transparency toggle should swap `SystemBackdrop` between `MicaBackdrop` and a solid surface brush. Both queued for Chunk 9 polish.

## Architectural shape

```
┌─────────────────────────────────────────────────────────────┐
│  Avelia.Shell.Windows (C# WinUI 3)                          │
│  Pages, ViewModels, Resources, theming, dialogs             │
│  ↕ (constructor-injected service interfaces)                │
├─────────────────────────────────────────────────────────────┤
│  Avelia.Core.Abstractions (F#) — interfaces & DTOs          │
│  Avelia.Core (F#) — domain types, DesignData, StubServices  │
├─────────────────────────────────────────────────────────────┤
│  [Future] Avelia.Persistence (SQLite)                       │
│  [Future] Avelia.Vcs.GitHub (Octokit + git CLI)             │
│  [Future] Avelia.Agent.ClaudeCode (subprocess driver)       │
└─────────────────────────────────────────────────────────────┘
```

ViewModels accept interfaces (e.g. `IWorkspaceService`); the shell wires `Stub*` implementations at startup. The shell never knows whether the data is fake or real — a one-line composition root swap lights up the real backend later.

---

## Chunk 0 — Theme tokens, brushes, Mica backdrop, window chrome

**Goal**: Replace the stock Welcome window with a Mica-backed window that has extended chrome ready for a TabView title bar + caption buttons. Theme tokens map the design's CSS variables to WinUI 3 ThemeResource keys.

**Files**:

- `src/Avelia.Shell.Windows/Themes/Tokens.xaml` (new) — `ResourceDictionary` with `ThemeDictionaries` (Light/Dark). Defines: `AveliaTextPrimaryBrush`, `AveliaTextSecondaryBrush`, `AveliaTextTertiaryBrush`, `AveliaSurfaceStrokeBrush`, `AveliaDividerStrokeBrush`, `AveliaCardBackgroundBrush`, `AveliaCardBackgroundSecondaryBrush`, `AveliaLayerFillTertiaryBrush`, `AveliaSubtleFillSecondaryBrush`, `AveliaAccentDefaultBrush`, `AveliaAccentTextBrush`, `AveliaSuccessBrush`, `AveliaSuccessBgBrush`, `AveliaWarningBrush`, `AveliaWarningBgBrush`, `AveliaDangerBrush`, `AveliaDangerBgBrush`, `AveliaDiffAddBgBrush`, `AveliaDiffAddStrongBrush`, `AveliaDiffDelBgBrush`, `AveliaDiffDelStrongBrush`. Pull values from `styles.css:9–127`.
- `src/Avelia.Shell.Windows/Themes/Typography.xaml` (new) — TextBlock styles for `AveliaCaption`/`Body`/`BodyStrong`/`BodyLg`/`Subtitle`/`Title` matching the `.t-*` ramp at `styles.css:182–189`. `AveliaMonoFontFamily` resource = `"Cascadia Code, Cascadia Mono, Consolas, Consolas"`.
- `src/Avelia.Shell.Windows/Themes/ControlStyles.xaml` (new) — first pass on reusable styles: `AveliaAccentButtonStyle`, `AveliaSubtleButtonStyle`, `AveliaIconButtonStyle`, `AveliaMergeButtonStyle` (green gradient — `styles-app.css:344–360`), `AveliaChipStyle`, `AveliaPivotStyle`.
- `src/Avelia.Shell.Windows/App.xaml` — merge the three new dictionaries above the `XamlControlsResources`.
- `src/Avelia.Shell.Windows/App.xaml.cs` — track `ElementTheme` on the active window via the new `ThemeService`; honor system theme on first launch.
- `src/Avelia.Shell.Windows/MainWindow.xaml` — remove the Greet placeholder. `ExtendsContentIntoTitleBar="True"`. Root `Grid` with `RowDefinition Auto` (title bar) + `*` (content).
- `src/Avelia.Shell.Windows/MainWindow.xaml.cs` — set `SystemBackdrop = new MicaBackdrop()` with fallback (mica unsupported → AcrylicBackdrop → solid). Wire `SetTitleBar` once tab strip exists in Chunk 2.
- `src/Avelia.Shell.Windows/Services/ThemeService.cs` (new) — implements `IThemeService`. Persists to settings later; in-memory for now. Sets `App.Current.RequestedTheme` and the override `AccentColor` resource.
- `src/Avelia.Shell.Windows/Helpers/WindowsSystemDispatcherQueueHelper.cs` (new) — boilerplate from the Windows App SDK Mica sample.

**Tests**:

- `tests/Avelia.Shell.Windows.Tests/ThemeServiceTests.cs` — toggling theme writes the expected `ElementTheme`; accent override updates the resource brush.

**Acceptance**: App launches, shows a translucent Mica window with custom caption buttons (or extended-chrome ready for them). Empty content area. No build warnings.

---

## Chunk 1 — F# domain types, IDs, service interfaces, design data, stub services

**Goal**: Define every domain type the design references, with smart constructors and DU exhaustiveness. Ship in-memory stub services backed by typed sample data so the shell can bind real types from day one.

**Files**:

- `src/Avelia.Core.Abstractions/Ids.fs` — extend with `RepositoryId`, `WorkspaceId`, `ConversationId`, `MessageId`, `RunId` (each a single-case DU around `Guid`). Provide `[<CompiledName("NewRepositoryId")>]` style helpers.
- `src/Avelia.Core.Abstractions/Primitives.fs` (new) — `BranchName`, `RepoPath`, `RelativePath` as single-case DUs with `tryCreate`/`create` smart constructors that validate (non-empty, no path traversal for paths, valid ref-name chars for branches).
- `src/Avelia.Core/Domain.fs` — extend with:
  - `WorkspaceStatus` DU: `Ready | Conflict | Archived | Draft | Active | Open`
  - `ModelChoice` DU: `Sonnet45 | Opus41 | Haiku45 | CustomModel of string`
  - `Repository` record: `Id`, `Name`, `Path: RepoPath`, `DefaultBase: BranchName`, `IsOpen: bool`
  - `Workspace` record: `Id`, `RepoId`, `Branch: BranchName`, `Base: BranchName`, `Status: WorkspaceStatus`, `DiffAdd: int`, `DiffDel: int`, `Agent: ModelChoice`, `LastUpdated: DateTimeOffset`, `PrId: PullRequestId option`
  - `MessageEvent` DU (event-sourced conversation primitive): `UserMessageAppended of UserMessage | AgentMessageAppended of AgentMessage | AgentErrorAppended of string | ToolBatchAppended of ToolBatch | ChangeNoteAppended of ChangeNote | AgentMarkdownAppended of AgentMarkdown`
  - Record types for each event payload (UserMessage with refs, AgentMessage, ToolBatch with `ToolCount`/`MessageCount`/`ToolKinds`, ChangeNote with file path + adds + dels, AgentMarkdown with heading/body/list)
  - `Conversation` record: `Id`, `WorkspaceId`, `Title: string`, `Messages: IReadOnlyList<MessageEvent>`, `LastSequence: int`
  - `DiffKind` DU: `Modified | Added | Deleted | Renamed of from: RelativePath`
  - `DiffFile` record: `Path: RelativePath`, `Add: int`, `Del: int`, `Kind: DiffKind`
  - `DiffLineKind` DU: `Context | Addition | Deletion`
  - `DiffLine` record: `LineNumber: int`, `Kind: DiffLineKind`, `Text: string`
  - `DiffHunk` record: `File: RelativePath`, `Header: string`, `Lines: IReadOnlyList<DiffLine>`
  - `CheckStatus` DU: `Passed | Failed | Warn | Running | Skipped`
  - `Check` record: `Name`, `Status`, `Description`, `Count: string`
  - `PullRequest` record: `Id: PullRequestId`, `Number: int`, `Title`, `Branch`, `Base`, `Status: PrStatus`, `Checks: IReadOnlyList<Check>`, `MergeReady: bool`
  - `PrStatus` DU: `Draft | Open | InReview | Approved | Merged | Closed`
  - `InboxItem` record + `InboxItemKind` DU (`Warn | Success | Info`)
- `src/Avelia.Core.Abstractions/Services.fs` — extend with the contract surface the shell needs. Public signatures use `IReadOnlyList<T>` / `IAsyncEnumerable<T>` / `Task<T>` (per CLAUDE.md F# style guide):
  - `IRepositoryService`: `ListAsync(ct)`, `AddAsync(RepoPath, ct)`, `RemoveAsync(RepositoryId, ct)`, `ObserveChanges(ct)` → `IAsyncEnumerable<RepositoryEvent>`
  - `IWorkspaceService`: `ListAsync(RepositoryId, ct)`, `GetAsync(WorkspaceId, ct)`, `CreateAsync(...)`, `ArchiveAsync(WorkspaceId, ct)`, `ObserveAll(ct)` stream
  - `IConversationService`: `GetAsync(WorkspaceId, ct)`, `PostUserMessageAsync(...)`, `ObserveMessages(ConversationId, ct)`
  - `IDiffService`: `GetWorkspaceDiffAsync(WorkspaceId, ct)`, `GetPullRequestDiffAsync(PullRequestId, ct)`, `GetHunksAsync(PullRequestId, RelativePath, ct)`
  - `IPullRequestService`: `GetForWorkspaceAsync(WorkspaceId, ct)`, `MergeAsync(PullRequestId, ct)`
  - `IRunService`: `ListAsync(WorkspaceId, ct)`, `StartAsync(...)`, `StopAsync(RunId, ct)`, `ObserveOutput(RunId, ct)` for terminal streaming
  - `IInboxService`: `ListAsync(ct)`, `MarkReadAsync(...)`, `ObserveAsync(ct)`
  - `IThemeService`: `Get/SetThemeAsync`, `Get/SetAccentAsync`, plus `ThemeChanged` event
  - `ISettingsService`: typed getter/setter pairs for density, transparency, default model, open-with-right-panel toggle
  - All Result-shaped failures return `OperationResult<T>` (defined in Abstractions) — a C#-friendly wrapper around `Result<T, ConductorError>` per CLAUDE.md §4
- `src/Avelia.Core.Abstractions/Errors.fs` (new) — `ConductorError` DU + `OperationResult<T>` shape with C#-friendly `IsSuccess` / `Value` / `Error` properties and a `Match` method
- `src/Avelia.Core/DesignData.fs` (new) — the typed equivalent of `data.jsx`: 8 repos, 5 workspaces across two of them, a sample `Conversation` with the 8-event transcript, 10 `DiffFile`s, 2 `DiffHunk`s, the 4 inbox items, the model choices and checks list. **This is the single source of mock data; do not duplicate in the shell.**
- `src/Avelia.Core/Stub/StubRepositoryService.fs` (new) and siblings for each service interface — back the interface with mutable `ResizeArray`s seeded from `DesignData`. Use `System.Threading.Channels` for `Observe*` streams so multiple subscribers work (per CLAUDE.md anti-pattern note: mutation is OK inside a service that wraps a Channel).
- `src/Avelia.Core/Composition.fs` (new) — a `module Composition` exposing `buildStubServices() : Services` where `Services` is a record of every service. The shell calls this once at startup.

**Tests** (`tests/Avelia.Core.Tests/`):

- Example-based: smart constructors accept/reject as expected; status transitions allowed/disallowed per `Workspace.canTransition`.
- Property-based: `MessageEvent` event-fold replay produces a `Conversation` whose `LastSequence` equals events folded; `WorkspaceId` generator never collides over 10k samples; `BranchName` round-trip serialize/deserialize equals original; replaying any prefix of events yields a valid conversation.
- Generators in `tests/Avelia.Core.Tests/Generators.fs` produce valid domain values only (no nonsense — per CLAUDE.md PBT discipline).

**Acceptance**: `./scripts/test-fast.ps1` green. F# core has no UI namespace references (`Microsoft.UI.*` not in any `.fsproj`). `DesignData` is consumable from C# without reflection (verify by reading one workspace from a smoke-test C# script in the shell startup, log to debug output).

---

## Chunk 2 — MainWindow chrome: TabView title bar + NavigationView rail + page Frame

**Goal**: Replace the Mica-only window from Chunk 0 with the actual app shell — workspace tabs in the title bar, a NavigationView rail, and a Frame for page content. Wired to `IWorkspaceService` so opening a workspace creates a tab.

**Files**:

- `src/Avelia.Shell.Windows/MainWindow.xaml` — full layout:
  - Title-bar row: horizontal StackPanel `[AppIcon] [TabView with workspace tabs and AddTabButton] [drag region] [Search button] [ThemeToggle button] [caption buttons placeholder space]`
  - Body row: `Grid` with NavigationView (PaneDisplayMode = `LeftCompact` toggleable to `Left`) → Frame
- `src/Avelia.Shell.Windows/MainWindow.xaml.cs` — `SetTitleBar(DragRegionElement)`; subscribe to `AppWindow.TitleBar` button-color tweaks; handle `NavigationView.SelectionChanged` to navigate the Frame.
- `src/Avelia.Shell.Windows/Controls/WorkspaceTabItemControl.xaml` (new) — `UserControl` for the contents of a `TabViewItem.Header`: status dot + branch + (active-tab-only) `/base` in mono. Match `styles-v2.css:48–119`.
- `src/Avelia.Shell.Windows/Controls/WorkspaceTreeItemControl.xaml` (new) — `UserControl` for a workspace entry inside the rail when the section group "Repositories" is expanded: status dot in icon slot, branch name primary, `+/-` chips trailing. Match `styles-v2.css:534–562`.
- `src/Avelia.Shell.Windows/Controls/StatusDot.xaml` (new) — small ellipse with status-color brush dispatch via converter.
- `src/Avelia.Shell.Windows/Converters/WorkspaceStatusToBrushConverter.cs` (new), `WorkspaceStatusToLabelConverter.cs`, `BranchPathSplitConverter.cs` (split `src/foo/bar.tsx` → folder + filename Runs), `DiffStatsVisibilityConverter.cs`.
- `src/Avelia.Shell.Windows/ViewModels/MainViewModel.cs` — rewrite. Takes `IWorkspaceService`, `IRepositoryService`, `IInboxService`, `IThemeService` via ctor. Exposes:
  - `ObservableCollection<WorkspaceTabViewModel> OpenTabs`
  - `WorkspaceTabViewModel? ActiveTab`
  - `ObservableCollection<NavRailItemViewModel> NavItems`
  - `ObservableCollection<RepoGroupViewModel> RepoGroups` (each with nested `WorkspaceItemViewModel`s)
  - `int InboxCount`, `bool IsRailExpanded`
  - Commands: `OpenWorkspaceCommand`, `CloseTabCommand`, `ToggleRailCommand`, `OpenAddRepoDialogCommand`, `OpenSettingsCommand`, `ToggleThemeCommand`
- `src/Avelia.Shell.Windows/ViewModels/WorkspaceTabViewModel.cs`, `NavRailItemViewModel.cs`, `RepoGroupViewModel.cs`, `WorkspaceItemViewModel.cs` (new) — `ObservableObject` subclasses with `[ObservableProperty]` source generators.
- `src/Avelia.Shell.Windows/Pages/PlaceholderPage.xaml` — minimal page used by Inbox/Pinned/History/Archive until their real pages ship in later chunks.

**Tests** (`tests/Avelia.Shell.Windows.Tests/`):

- VM tests: opening a workspace twice doesn't duplicate the tab. Closing the active tab activates the previous one. Theme toggle flips the service.
- E2E (deferred to Chunk 9 batch): drives the tab close button.

**Acceptance**: App launches. Mica + workspace tabs + nav rail + caption buttons all coexist cleanly. Drag region works (you can drag the window by the title-bar empty space). Nav between sections changes the Frame's page (placeholder content fine for now). Theme toggle in title bar flips Light/Dark live without restart.

---

## Chunk 3 — Workspace page, center pane: pivot tabs + chat scroll + composer

**Goal**: Render the active workspace's conversation — pivot thread switcher at top, scrollable transcript with all six message shapes, composer pinned at bottom. Match `v2-views.jsx:267–302` (V2Center) and `workspace-view.jsx:82–197`.

**Files**:

- `src/Avelia.Shell.Windows/Pages/WorkspacePage.xaml` — root `Grid` with two columns (center + right). For this chunk, only center is real; right column is empty / next chunk. Center is a `Grid` with: pivot row (Auto) + chat row (`*`) + composer row (Auto).
- `src/Avelia.Shell.Windows/Pages/WorkspacePage.xaml.cs` — receives a `WorkspaceId` via `Frame.Navigate(..., new WorkspaceNavArgs(id))`; resolves `WorkspaceViewModel` and assigns to DataContext.
- `src/Avelia.Shell.Windows/Controls/ChatPivot.xaml` — custom pivot strip mimicking `.v2c-pivot`: text + icon + 3px accent underline. Use `SelectorBar` if it offers underline customization; otherwise an ItemsControl with toggle behavior.
- `src/Avelia.Shell.Windows/Controls/ConversationView.xaml` — `ItemsRepeater` with a `DataTemplateSelector` selecting one of six templates: `AgentMessageTemplate`, `UserMessageTemplate`, `AgentErrorTemplate` (red banner), `ToolBatchTemplate` (collapsed batch), `ChangeNoteTemplate` (filename + chips), `AgentMarkdownTemplate` (heading + body + list).
- `src/Avelia.Shell.Windows/Controls/CodeRefRun.cs` (new) — helper to split text by `/(@\S+\.\w+)/` and render `Run` elements, accent-text styling for refs (match `workspace-view.jsx:96`).
- `src/Avelia.Shell.Windows/Controls/Composer.xaml` — `Grid` with `TextBox` (multi-line, accept-Enter-with-Shift), bottom toolbar (`ModelBadge` UserControl + Link-issue button + spacer + Attach + Tools + Send). Send is the accent `Button` from Chunk 0.
- `src/Avelia.Shell.Windows/Controls/ModelBadge.xaml`, `Controls/Chip.xaml`, `Controls/KbdChip.xaml` (new) — small reusable controls.
- `src/Avelia.Shell.Windows/ViewModels/WorkspaceViewModel.cs` — exposes the conversation, the current thread (pivot), composer text, `SendMessageCommand`. Posts via `IConversationService.PostUserMessageAsync`; updates from `ObserveMessages` stream marshalled to the UI thread via `DispatcherQueue.TryEnqueue`. Cache the dispatcher in the ctor (CLAUDE.md gotcha #4).
- `src/Avelia.Shell.Windows/ViewModels/MessageViewModel.cs` and subclasses — discriminated VM type per `MessageEvent` case so the DataTemplateSelector picks correctly.

**Tests**:

- VM tests: `WorkspaceViewModel` posts message → `IConversationService.PostUserMessageAsync` invoked with correct arguments; cancellation token threads through.
- Property tests: `MessageViewModel.fromEvent` round-trips through every `MessageEvent` case (exhaustiveness asserted via compiler-checked match).
- Visual stability test (deferred): assert no layout shift while streaming agent tokens.

**Acceptance**: Open the seeded workspace, see the 8-entry transcript with proper styling: error banner red, code refs accent, tool-batch collapsed row with icons, change note as bordered card, agent-md with ordered list, user message in a bordered card. Composer accepts text. Pivot tabs switch (single thread for now so they all show the same conversation — refactor when multi-thread lands).

---

## Chunk 4 — Workspace page, right pane: PR header + Changes/Files + Terminal

**Goal**: Fill the right column of `WorkspacePage.xaml` with the PR summary, file list, and mock terminal. Match `v2-views.jsx:307–396` (V2Right).

**Files**:

- `src/Avelia.Shell.Windows/Controls/PrHeader.xaml` — "PULL REQUEST" caps label + PR# link + base mono ← branch mono + Merge button + stats row (files · +/− · checks).
- `src/Avelia.Shell.Windows/Controls/FileChangeList.xaml` — `ItemsRepeater` with `Button` rows: folder/filename split (mono, tertiary/primary), `+N` / `-N` chips, trailing chevron. Click → navigates Frame to `PrReviewPage` (Chunk 6) with the file pre-selected.
- `src/Avelia.Shell.Windows/Controls/TerminalPanel.xaml` — sticky bottom area: tab strip (Run / Terminal) + Run-button overflow on the right, body with mono prompt mockup `→ {base} git:({branch})` + blinking cursor. Blink via Storyboard. Real run wiring deferred — bind to `IRunService.ObserveOutput` later.
- `src/Avelia.Shell.Windows/Pages/WorkspacePage.xaml` — right column gets a vertical `Grid` with rows for PrHeader / file-list pivot / file list / TerminalPanel.
- `src/Avelia.Shell.Windows/ViewModels/PrPaneViewModel.cs`, `DiffFileViewModel.cs`, `TerminalPanelViewModel.cs` — `PrPaneViewModel` pulls from `IPullRequestService.GetForWorkspaceAsync` + `IDiffService.GetWorkspaceDiffAsync` at navigation time.

**Tests**:

- VM tests: PrPaneViewModel exposes correct totals (sum of file +/-); selecting a file raises `FileSelected` event; Merge command only enabled when `MergeReady`.

**Acceptance**: Right pane mirrors the seeded PR for the active workspace: PR #1432, archive-in-repo-details ← kampala-v3, 10 files, +312 −332, 10 checks. Selecting a file navigates to the PR review page (next chunk). Terminal cursor blinks.

---

## Chunk 5 — Settings page

**Goal**: A full Settings experience reachable from the rail and the title-bar gear. Side navigation + cards for Appearance / Agents & Models / Profile (and stubs for the rest). Match `screens.jsx:135–325`.

**Files**:

- `src/Avelia.Shell.Windows/Pages/SettingsPage.xaml` — back-chevron header + `Grid` with side-nav column + main content column. Side nav is an `ItemsRepeater` of `SettingsSectionItem` rows (icon + label + active accent bar).
- `src/Avelia.Shell.Windows/Pages/SettingsSubpages/AppearanceSubpage.xaml` — Theme segmented (Light/Dark/System), Accent swatches (6 colors per design at `screens.jsx:182–188`), Transparency `ToggleSwitch`, density segmented, "Open with right panel" toggle.
- `Pages/SettingsSubpages/AgentsSubpage.xaml` — three model cards (Sonnet 4.5 default, Opus 4.1, Haiku 4.5) + extended-thinking toggle.
- `Pages/SettingsSubpages/ProfileSubpage.xaml` — avatar + name + email + "Manage account" subtle button.
- `Pages/SettingsSubpages/PlaceholderSubpage.xaml` — used for Repositories / Keyboard / Notifications / Privacy / Updates / About until their content is designed.
- `src/Avelia.Shell.Windows/ViewModels/SettingsViewModel.cs` — selects active subpage VM. `IThemeService` for live theme switching; `ISettingsService` for the rest.
- `src/Avelia.Shell.Windows/Controls/SettingsCard.xaml` — reusable: leading icon + body (title/desc) + trailing action slot. Matches `.settings-card` (`styles-app.css:557–570`).

**Tests**:

- VM tests: changing theme via segmented updates `IThemeService` + `App.RequestedTheme` flips. Changing accent updates `AccentDefaultBrush` resource.

**Acceptance**: Open Settings via nav rail; see all 9 sections in the side nav. Appearance fully wired: theme + accent + toggles change live. Agents and Profile show their cards. Other sections show a placeholder card with the section name.

---

## Chunk 6 — PR Review page with unified diff viewer

**Goal**: A diff viewer that renders the typed `DiffHunk`/`DiffLine` data. Two-column layout: file list (left) + diff pane (right) with header + checks InfoBar + monospace hunks. Match `screens.jsx:331–418`.

**Files**:

- `src/Avelia.Shell.Windows/Pages/PrReviewPage.xaml` — back chevron + title row (PR# + name + status pill + Compare/Merge buttons). Body is `Grid` 280px / `*`. Left = `Controls/PrFileTree`; right = `Controls/UnifiedDiffViewer`.
- `src/Avelia.Shell.Windows/Controls/PrFileTree.xaml` — file rows with M/A/D kind badge + path split + +/− chips. Match `.pr-file-row` styles.
- `src/Avelia.Shell.Windows/Controls/UnifiedDiffViewer.xaml` — header card (file path mono + +N/-N chips + Unified/Split SegmentedControl + "View file" button) + InfoBar ("All checks passed") + hunks list. Each hunk: hunk-header row (accent text on subtle bg) + lines.
- `src/Avelia.Shell.Windows/Controls/DiffLineRow.xaml` — three-column row: line number (right-aligned tertiary mono with kind-specific bg) + sign (+/-) + source (mono, no wrapping). Add/del row bgs use the `AveliaDiffAdd*` / `AveliaDiffDel*` brushes. Implement as a templated `Control` or `UserControl` with a `DiffLine` DP — keep visual tree shallow because diff lists can be long.
- `src/Avelia.Shell.Windows/ViewModels/PrReviewViewModel.cs`, `DiffPaneViewModel.cs` — load hunks on file selection via `IDiffService.GetHunksAsync`.
- Action row footer: Request changes / Comment / Approve buttons.

**Tests**:

- Property test: any `DiffHunk` round-trips through the VM (lines preserved, kinds preserved, no off-by-one in line numbering).
- VM test: selecting a file in the tree triggers `LoadHunksAsync`.

**Acceptance**: From a workspace's right pane file row, navigate to PR Review. See the 10 files in the left tree, the seeded `RepositoryDetailsDialog.tsx` hunks rendered with proper +/− coloring, working line numbers, and the green "All checks passed" InfoBar.

---

## Chunk 7 — Inbox page

**Goal**: A short list of inbox items (warning/success/info) with click-to-open behavior. Match `app-v2.jsx:223–269`.

**Files**:

- `src/Avelia.Shell.Windows/Pages/InboxPage.xaml` — page header ("Inbox", "N unread · N total") + card containing `ItemsRepeater` of `InboxItemRow` buttons.
- `src/Avelia.Shell.Windows/Controls/InboxItemRow.xaml` — leading kind-icon square (warn/success/info colored bg) + title/desc + time + trailing chevron.
- `src/Avelia.Shell.Windows/ViewModels/InboxViewModel.cs` — loads from `IInboxService.ListAsync`; clicking an item with a `WorkspaceId` raises a navigation request that the shell turns into `OpenWorkspace`.

**Tests**:

- VM test: clicking an inbox item with `WorkspaceId` set publishes a `WorkspaceOpenRequested` event.

**Acceptance**: Inbox section in nav rail shows the 4 seeded items; clicking the "archive-in-repo-details ready to merge" item opens that workspace.

---

## Chunk 8 — Add Repository ContentDialog

**Goal**: A WinUI 3 `ContentDialog` with three tabs (Local folder / Git URL / Clone from GitHub), each with the inputs the design shows. Recent-repos list under Local. SSH-detected InfoBar under Git URL. Match `screens.jsx:8–130`.

**Files**:

- `src/Avelia.Shell.Windows/Dialogs/AddRepositoryDialog.xaml` — `ContentDialog` subclass with a `SelectorBar` for the three tabs, conditional content per selection.
- `src/Avelia.Shell.Windows/Dialogs/AddRepositoryDialog.xaml.cs` — `ShowAsync` invoked from `MainViewModel.OpenAddRepoDialogCommand`. Wires up Browse button → `FolderPicker`.
- `src/Avelia.Shell.Windows/ViewModels/AddRepositoryViewModel.cs` — state per tab (path, recent-repos list, URL, clone-to, GitHub search). `AddCommand` calls `IRepositoryService.AddAsync` and closes on success.
- `src/Avelia.Shell.Windows/Controls/RecentRepoRow.xaml` — folder icon + name/path + count.

**Tests**:

- VM test: `AddCommand` disabled when local path empty; calling Add invokes `IRepositoryService.AddAsync` with the typed `RepoPath`.
- Property test: `RepoPath.tryCreate` rejects paths containing `..` traversal.

**Acceptance**: Title-bar `+` button and rail "Add repository" both open the dialog. Browse opens a folder picker. Adding closes the dialog and the new repo appears in the rail tree.

---

## Chunk 9 — Accessibility, automation, polish, test pass

**Goal**: Close the seven UX surfaces in CLAUDE.md §UX testing requirements. Make the shell screen-reader-navigable and feature-tested.

**Tasks**:

- Set `AutomationProperties.Name` on every focusable control (every nav item, tab, button, file row, message). Add `AutomationProperties.HelpText` where ambiguous.
- Set sensible tab order in each page; verify with the accessibility-tree enumerator test.
- Wire one E2E test per major flow:
  - Open app → open seeded workspace → see transcript → type in composer → simulated send updates transcript
  - Open Settings → switch theme → close → reopen workspace → assert theme persisted
  - Open inbox → click item → workspace opens
  - Open Add Repo dialog → fill local path → Add → repo appears in rail
- Visual-stability test: stream synthetic agent tokens into the conversation; assert no layout shift in the file list or composer.
- Cancellation tests: open a workspace, navigate away mid-`ObserveMessages` subscription, assert clean dispose.
- Property tests: `MessageEvent` fold round-trip; `WorkspaceStatus.canTransition` exhaustiveness; `DiffLine` serialize round-trip.
- `./scripts/format.ps1` no-op; `./scripts/test-fast.ps1` green; `./scripts/test-integration.ps1` green for non-DB tests.

**Acceptance**: CI green. Tab through the shell with no missing-name warnings. Screen reader announces every interactive element.

---

## Chunk 10 (FOLLOW-UP — not in this initial implementation) — Real backend

Documented here so the contract from Chunk 1 isn't designed in a vacuum. **Out of scope for this work; flagged for the next planning round.**

- `Avelia.Persistence/Sqlite.fs` — schema for repos / workspaces / conversations / messages / runs. Hydrate on startup; persist on every event. Migrations versioned.
- `Avelia.Vcs.GitHub` — Octokit-based GitHub adapter for PR listing/merging; `git` CLI subprocess wrapper for branch/diff/worktree operations.
- `Avelia.Agent.ClaudeCode` — subprocess driver that pipes Claude Code's JSON stream events into `IConversationService.PostXxxAsync`. Captures tool calls into `ToolBatch` events.
- `Avelia.Core/Composition.fs` gains a `buildRealServices(config) : Services` variant; shell's `App.OnLaunched` picks stub vs real based on a config flag.
- File watcher on worktree paths → drives `IWorkspaceService.ObserveAll` so a `git checkout` on disk updates the UI without restart.
- Integration tests against a real SQLite file (no mocks — per AGENTS.md ground rule).

---

## Critical files (touched by this plan)

**F# core** (Chunks 0–1 mostly; later chunks consume):

- `src/Avelia.Core.Abstractions/Ids.fs` (extend)
- `src/Avelia.Core.Abstractions/Primitives.fs` (new)
- `src/Avelia.Core.Abstractions/Services.fs` (extend)
- `src/Avelia.Core.Abstractions/Errors.fs` (new)
- `src/Avelia.Core/Domain.fs` (extend)
- `src/Avelia.Core/DesignData.fs` (new)
- `src/Avelia.Core/Stub/*` (new — one file per service)
- `src/Avelia.Core/Composition.fs` (new)

**C# shell** (every chunk):

- `src/Avelia.Shell.Windows/Themes/{Tokens,Typography,ControlStyles}.xaml`
- `src/Avelia.Shell.Windows/App.xaml{.cs}` (rewrite)
- `src/Avelia.Shell.Windows/MainWindow.xaml{.cs}` (rewrite)
- `src/Avelia.Shell.Windows/Pages/{Workspace,Settings,PrReview,Inbox,Placeholder}Page.xaml`
- `src/Avelia.Shell.Windows/Pages/SettingsSubpages/{Appearance,Agents,Profile,Placeholder}Subpage.xaml`
- `src/Avelia.Shell.Windows/Dialogs/AddRepositoryDialog.xaml`
- `src/Avelia.Shell.Windows/Controls/*` (many — listed per chunk)
- `src/Avelia.Shell.Windows/Converters/*`
- `src/Avelia.Shell.Windows/Services/ThemeService.cs`
- `src/Avelia.Shell.Windows/Helpers/WindowsSystemDispatcherQueueHelper.cs`
- `src/Avelia.Shell.Windows/ViewModels/{Main,WorkspaceTab,NavRailItem,RepoGroup,WorkspaceItem,Workspace,Conversation,Message,PrPane,DiffFile,TerminalPanel,Settings,PrReview,DiffPane,Inbox,AddRepository}ViewModel.cs`

## Existing utilities to reuse

- `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) — already referenced; rely on it instead of writing custom INPC.
- `Microsoft.UI.Xaml.Controls.NavigationView` / `TabView` / `InfoBar` / `ContentDialog` / `SelectorBar` / `Expander` — built-in WinUI 3 controls; style rather than reinvent.
- `Microsoft.UI.Xaml.Media.MicaBackdrop` — built-in (Windows App SDK 1.5+).
- `task { ... }` and `taskSeq { ... }` F# computation expressions — already idiomatic in `Avelia.Core`.
- `WindowsAppSDK` `AppWindow.TitleBar` for caption-button color theming.
- `Cascadia Code` font — ships with Windows 11; cite as font family with fallbacks.

## Verification (end to end, after Chunks 0–9)

1. `./scripts/clean.ps1 && ./scripts/bootstrap.ps1` — restore + build clean from scratch.
2. `./scripts/test-fast.ps1` — F# property + VM tests pass under 30s.
3. `./scripts/test-integration.ps1` — non-E2E integration tests pass.
4. `cd src/Avelia.Shell.Windows && winapp run .` — app launches in Mica with the seeded workspace open in a tab.
5. Manual smoke: switch theme (title-bar button) — Mica reflows, accent colors update. Switch accent (Settings → Appearance) — swatch active state and selected nav indicator change. Open Add Repository dialog, fill a local path, Add — new repo appears in rail. Click an inbox notification — opens the linked workspace. Click a diff-file row in right pane — navigates to PR Review with that file pre-selected.
6. `./scripts/format.ps1` — diff is empty.
7. CHANGELOG updated per chunk.

## Estimated chunk sizes (rough — for sequencing, not contracts)

| Chunk | Surface | Approx LOC |
|------:|---------|-----------:|
| 0 | Theme + chrome + Mica | ~600 XAML, ~150 C# |
| 1 | F# domain + stubs + tests | ~1200 F#, ~400 tests |
| 2 | TabView + NavView + Frame | ~500 XAML, ~600 C# |
| 3 | Center pane: pivot+chat+composer | ~400 XAML, ~500 C# |
| 4 | Right pane: PR+files+terminal | ~300 XAML, ~350 C# |
| 5 | Settings page | ~500 XAML, ~400 C# |
| 6 | PR review + diff viewer | ~400 XAML, ~400 C# |
| 7 | Inbox page | ~150 XAML, ~150 C# |
| 8 | Add Repository dialog | ~250 XAML, ~300 C# |
| 9 | A11y + tests + polish | ~300 test code |

Chunks are reviewable independently. Each leaves the build green; Chunks 0–2 must land in order (they create the shell), 3–8 can interleave once 2 is in.
