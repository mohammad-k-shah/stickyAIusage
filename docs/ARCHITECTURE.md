# Architecture

One page on how Sticky AI Usage fits together: read it before your first change. Everything here is in the
repository; nothing depends on a service of ours (there isn't one).

```text
                       Claude Code (terminal, Desktop Code tab, VS Code)
                                  │ its own figures and events
                                  ▼
                  plugin/  (JavaScript hooks module, no dependencies)
                                  │ feed/claude-<session>.json, feed/plugin.json
                                  ▼
  ┌──────────────────────── StickyAIusage.Core (net10.0, any OS) ───────────────────────┐
  │ usage sources ─► provider ─► UsageFeed ─► view models ─► "what to show", as data      │
  │ transcripts (index + reader) · prices · pace forecast · alerts · sharing · Health     │
  │ the pace forecast writes feed/forecast.json ─► read back by the plugin's /pace, band  │
  └───────────────┬────────────────────────────────────────────────────┬─────────────────┘
                  ▼                                                    ▼
   StickyAIusage (WPF, Windows 11)                       StickyAIusage.Mac (Avalonia, macOS 15+)
   taskbar pill, tray icon, toasts                       menu bar pill, menu, banners
```

## The three projects (and the plugin)

| Project | What lives there | Rule |
|---|---|---|
| `src/StickyAIusage.Core` | Everything that decides *what* to show: reading usage, sessions, costs, the pace, alerts, card text, Health, where the Windows pill docks (as pure geometry) | Plain `net10.0`, no UI types, unit-tested on any OS |
| `src/StickyAIusage` | The Windows app (WPF): windows, the tray icon, the taskbar's native calls (`Native/`), toasts, the clipboard | Maps Core's view models to controls; no decisions of its own |
| `src/StickyAIusage.Mac` | The macOS app (Avalonia): the menu bar item, windows, the login item (a LaunchAgent), `osascript` banners and clipboard | Same as Windows: if both apps need it, it goes in Core |
| `plugin/` | The Claude Code companion plugin `sticky-usage`: writes the feed, adds `/pace`, the usage tool, the band above the prompt, session lights and the budget guard | Pure helpers (`feed.js`, `status.js`, `pace.js`, `state.js`, `guard.js`) with node tests beside them; `register.js` wires them to Claude Code |

Warnings are errors in every project (`Directory.Build.props`), nullable is on, and there is no third-party
code in Core.

## Where usage comes from: sources, trust and the provider

Every number carries a **trust level**, and the UI and docs name it:

- **Official** — a documented Claude Code interface (the plugin's `$.session.usage()`).
- **Derived** — worked out from a tool's own files on your computer (Claude Code transcripts, Codex's session
  logs), whose format the tool may change.
- **Unofficial** — an undocumented endpoint. Opt-in only, and labelled wherever it is shown.

Claude's limits come from an `IClaudeUsageSource` chain, chosen in Settings → Usage source (`ClaudeSource`):

| Mode | Sources, most trusted first |
|---|---|
| `feed` "Plugin" (new installs) | `ClaudeFeedUsageSource` — the plugin's files in `feed/` (Official) |
| `auto` "Automatic" (upgrades keep it) | the feed, then `ClaudeOAuthUsageSource` whenever the feed has nothing current (Unofficial) |
| `legacy` | `ClaudeOAuthUsageSource` alone |
| `off` | none |

`ClaudeUsageProvider` asks the sources most trusted first and skips the less trusted ones while a better one has a
fresh reading. Of what it collects, numbers beat "no numbers, and why"; then the newest non-stale reading wins, and
within two minutes the more trusted one. `AppServices.Create` builds only the sources the mode needs: in Plugin
mode the endpoint source and its HTTP client are never built (the only other HTTP client is the price-list check,
off unless the user turns it on).

Other tools beside Claude (Codex today) implement `IProviderUsageReader` and return the provider-neutral
`ProviderUsage` (windows, an optional spend cap, the source and its trust). [PROVIDERS.md](PROVIDERS.md) is the
guide.

## The feed bridge

The plugin and the widget never talk directly; they share a folder (`feed/` in the widget's data folder, see
[DATA-SOURCES.md](DATA-SOURCES.md)):

1. After each Claude Code turn (and at a session's start and end) the plugin writes `claude-<session>.json`
   (schema 1): the limits exactly as Claude Code reported them, when it reported them (`rateLimitsAt`), the
   context, Claude Code's own cost, and the session's state for the session lights. It also writes `plugin.json`
   (which plugin and Claude Code version wrote last).
2. The widget watches the folder and refreshes the moment a record changes. The limits are the account's, so the
   record with the newest `rateLimitsAt` wins across sessions; a reading over three minutes old shows "As of".
3. The widget writes its pace forecast back as `forecast.json`; the plugin's `/pace`, band and tool use its limit
   time while it is under two minutes old (so the pill and `/pace` agree), and their own `pace()` otherwise.

The pace math exists twice, C# in `Forecasting/PaceCalculator.cs` and JavaScript in `plugin/hooks/pace.js`, and
both run the same vectors in `tests/vectors/pace/`.

## Where each kind of logic lives (Core)

| Folder | What |
|---|---|
| `Models/` | `UsageSnapshot` (Claude's reading), `ProviderUsage` and `UsageWindow` (any tool's), `SessionInfo`, `FeedSession` |
| `Services/` | The sources and providers, `AppServices` (builds the object graph both apps use) and `AppServicesHost` (rebuilds it between refreshes when Settings change), `UsageFeed` (one refresh: usage, sessions, pace, alerts), `TranscriptIndex` + `ClaudeSessionLogReader` (watch `projects/` once, then read only appended bytes), `ModelCatalog` + `PriceTable` (prices from `data/models.json`), `GitInfoReader`/`GitInfoCache`, `SettingsService`, `AppPaths` (every folder, profiles, environment overrides), `AtomicFile`, `DiagnosticLog`, `SelfTest`, `CodexUsageProvider` |
| `Forecasting/` | The usage journal and the pace forecast |
| `Alerts/` | `AlertPlanner` (pure: readings + settings + state + now → which notifications), its state file and the runner |
| `ViewModels/` | Pill and flyout text for the three layouts (monthly spend, plan limits, unknown), session cards and their field catalog (`SessionFields`), session lights, the health line, docking geometry (`DockPlacement`) |
| `Sharing/`, `Stats/` | The share cards' content (every string a card shows) and the year card's daily rollups |
| `Health/` | The first-run window's steps, Settings → Health's checks, the plugin installer's commands |
| `Updates/` | The daily "is there a newer version?" check (Check for updates on; shown, never installed), release versions, the update command per install method |
| `Demo/` | `--demo`: two scripted minutes of made-up usage, sessions and alerts, built with `AppServices.CreateDemo` |

A usage state that is new must appear in all three layouts and in both apps; the view-model tests check the text,
the screenshot tests check that it draws.

## Time, files and failure

- **Time is injected.** Anything that depends on the clock takes a `now` or a clock function, and anything that
  depends on the date (the year card's season) takes its own clock, so tests never sleep and never break in
  December.
- **Files are written atomically** (`AtomicFile`: a temp file beside the target, then a rename), and every
  persisted format has a schema number or a reader that tolerates older files.
- **Background work never throws.** Services return a reading that says why it has no numbers and log the cause
  to `diagnostics.log`; the UI shows that reason somewhere (the health line, Settings → Health).

## How the UI tests render both apps

- `tests/StickyAIusage.Tests` — Core, any OS. Every test project points the data and feed folders at temp folders
  in a module initializer, so a test run never touches your real ones, and pins `en-US` so times read the same
  everywhere.
- `tests/StickyAIusage.Mac.UiTests` — the Avalonia windows rendered headless (Skia) to PNG, **on any OS**, so a
  Windows machine checks the Mac screens too.
- `tests/StickyAIusage.UiTests` — the WPF windows rendered off-screen (`RenderTargetBitmap`) and the tray icon,
  Windows only. A locked Windows session draws blank pictures; the README's pictures refuse to be replaced by
  blank ones.
- Both UI projects build the real windows from Core's view models with fixed data and a fixed clock, and set
  `UI_SCREENSHOT_DIR` (keep the PNGs) or `UI_MARKETING_DIR` (the README's pictures, listed in
  `tests/Shared/ReadmePictures.cs`). CI uploads the screens of every run.
- `--self-test` (both apps) starts the real app in a temporary profile without a window and checks that it can
  load, draw and read; CI runs it on the published files before a release.

## Start here

- A new session-card field: `ViewModels/SessionFields.cs` ([CONTRIBUTING.md](CONTRIBUTING.md#add-a-session-card-field)).
- A price or a new model: `data/models.json` ([CONTRIBUTING.md](CONTRIBUTING.md#update-the-price-list)).
- Another AI tool: [PROVIDERS.md](PROVIDERS.md).
- What the app reads, writes and sends: [DATA-SOURCES.md](DATA-SOURCES.md).
