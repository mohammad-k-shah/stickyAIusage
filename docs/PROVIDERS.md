# Adding a tool beside Claude

Sticky AI Usage shows Claude's limits on the pill and, under them in the panel, a card for each other AI tool it
finds on the computer. Codex is the first one. This page explains how such a provider works, with Codex as the
worked example, so another tool can be added as **one class plus its tests** (and a few lines of wiring).
[ARCHITECTURE.md](ARCHITECTURE.md) shows where providers sit in the whole.

## The rules every provider follows

- **Local first.** Read what the tool already writes on the computer. No sign-in tokens, no undocumented
  endpoints by default.
- **Undocumented means opt-in.** A provider that needs a sign-in token or an undocumented endpoint is **off by
  default**, says `Unofficial` wherever its numbers are shown, and says what it reads next to its switch. Pull
  requests that turn such a provider on by default are not accepted.
- **Say where the numbers come from, honestly.** Every reading carries a trust level: `Official` (a documented
  interface of the tool), `Derived` (worked out from the tool's own files, whose format may change) or
  `Unofficial` (an undocumented endpoint). Pick the lowest one that is true. The panel shows a source line under
  the card's title, and [DATA-SOURCES.md](DATA-SOURCES.md) and the README say exactly what is read.
- **Absent means invisible.** A tool that isn't installed leaves no trace: no card, no chip, nothing in the log, no
  Settings warning beyond "not found on this computer".
- **Never throw.** A broken file loses only what it broke; the provider returns a reading whose health says why it
  has no numbers.
- **Read only what it needs.** Only the tool's own folder for usage data, and only the part of a file that holds the
  answer. Never a model call, never the user's prompts.

## The interface

```csharp
public interface IProviderUsageReader
{
    string ProviderId { get; }       // "codex": settings and alerts use it
    string DisplayName { get; }      // "Codex"
    bool IsPresent { get; }          // cheap; asked on every refresh
    Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default);
}
```

`ProviderUsage` (`src/StickyAIusage.Core/Models/ProviderUsage.cs`) is the provider-neutral reading:

| Part | What |
|---|---|
| `ProviderId`, `DisplayName`, `PlanName` | Who it is ("Codex", "Plus") |
| `Windows` | A list of `UsageWindow(Kind, PercentUsed, ResetsAt, Length)`; standard kinds are in `UsageWindowKinds` (`five_hour`, `seven_day`, …), any other string works |
| `Spend` | An optional `SpendCap` (used and limit in dollars, percent, reset, period) |
| `ObservedAt` | When the numbers were true (not when you read the file) |
| `SourceId`, `Trust` | Where they came from, and `SourceTrust.Official` / `Derived` / `Unofficial` |
| `Health`, `HealthDetail` | `Ok`, `Stale`, `NeedsSignIn` or `Unavailable`, with one line saying why |
| `SourceLabel` | The line under the card's title ("from Codex's local session log") |
| `WindowLabels` | Names for windows whose kind isn't a standard one ("Daily") |
| `WholePointsOnly` | The tool reports whole percentage points; the pace then waits for two points of rise |

Everything else comes for free: the card in the panel (rows with bars, resets and pace lines), the pill chip
("Codex 91%" when the tool is past the orange threshold and closer to a limit than Claude), the pace forecast,
the notifications (named after the tool), and the on/off switch (`DisabledProviders` in `settings.json`).

## Step by step

1. **The reader:** `src/StickyAIusage.Core/Services/<Tool>UsageProvider.cs`, implementing `IProviderUsageReader`.
   Its constructor takes everything it touches (the tool's folder, a clock, a file-system seam if it reads files),
   so its tests use fakes; nothing in it reads the real clock or a fixed path.
2. **Where it looks:** add the tool's folder to `AppPaths` (with the tool's own environment variable if it has
   one, like `CODEX_HOME`), so profiles and tests can move it.
3. **Wire it in:** add it to the `others` list in `AppServices.CreateDefault`. That is all the refresh, the card,
   the chip, the pace and the alerts need.
4. **Its switch:** a row in both Settings windows (`src/StickyAIusage/SettingsWindow.xaml(.cs)` and
   `src/StickyAIusage.Mac/SettingsWindow.axaml(.cs)`) that calls `SetProviderEnabled(<id>, …)`, with a hint from Core
   saying what it reads or that the tool wasn't found (see `OtherProviders.CodexSettingsHint`). `DisabledProviders`
   lists the tools that are switched off, so a provider is on unless its id is there; an opt-in provider must start
   off, which means adding its id to `DisabledProviders` for new and existing settings in `SettingsService` (a
   settings-format step). Say so in the issue first, and the maintainer will help with that part.
5. **Tests:** `tests/StickyAIusage.Tests/<Tool>UsageProviderTests.cs` with hand-written fixtures in
   `tests/StickyAIusage.Tests/Fixtures/<tool>/`: the current format, an older one if the tool changed it, a file with
   no limits, broken lines, a missing folder (nothing shown). Fixtures are made up: never paste a real log with
   your prompts or paths in it. If the reader takes a file-system seam, make the fake fail the test when anything
   outside the tool's usage folder is touched.
6. **Screens:** a screenshot of the card in both UI test projects (`tests/StickyAIusage.UiTests`,
   `tests/StickyAIusage.Mac.UiTests`), so CI draws it.
7. **Docs:** a section in [DATA-SOURCES.md](DATA-SOURCES.md) (what it reads, trust, network, how to turn it off), a
   line in the README's privacy section and settings table, and a CHANGELOG entry.

Optional and welcome: an item in Settings → Health (`Health/HealthCheck.cs`, see the Codex item) and a check in
`--self-test` (`Services/SelfTest.cs`, see `CheckCodex`) that reads a sample back.

## The worked example: Codex

`CodexUsageProvider` reads the limits Codex writes into its own session logs on the computer (`$CODEX_HOME/sessions`,
by default `~/.codex/sessions`): only the end of the newest files, never Codex's sign-in or settings. Its trust is
`Derived`, since the format is Codex's own rather than a documented interface, and a reading older than ten minutes
is shown "as of".

Its tests are the reference for everything else: `CodexUsageProviderTests`, with made-up fixtures in
`tests/StickyAIusage.Tests/Fixtures/codex/` for every shape the reader expects, and a guarded file-system seam that
fails a test touching anything outside `sessions/`; `OtherProvidersTests` cover the card, the chip, the pace, the
alerts and the Settings switch. If Codex changes its format, a pull request with a new fixture is the fastest fix.

## Before you open the pull request

- [ ] Absent tool: no card, no chip, no log line, Settings says "not found".
- [ ] The trust level is the lowest one that is true; an undocumented endpoint or a token is opt-in and `Unofficial`.
- [ ] The tests run without the tool installed and without a network.
- [ ] `dotnet test tests/StickyAIusage.Tests` and both UI test projects pass; the build has no warnings.
- [ ] DATA-SOURCES.md, the README rows and the CHANGELOG say what it reads.
