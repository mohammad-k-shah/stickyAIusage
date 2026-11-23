# Contributing

Issues and pull requests are welcome: bug reports, ideas, a new field for the session card, or a plan layout
that doesn't look right on your account (real-world plan shapes are the hardest part to test).

**Start here:**

| Page | Read it when |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Before your first code change: the three projects, where usage comes from, the plugin's feed, where each kind of logic lives, how the tests draw both apps |
| [PROVIDERS.md](PROVIDERS.md) | You want another AI tool beside Claude and Codex |
| [DATA-SOURCES.md](DATA-SOURCES.md) | Your change reads, writes or sends anything new (it must get a line there) |
| [ROADMAP.md](ROADMAP.md) | You're looking for something bigger to take on |

**Looking for a first issue?** Issues labelled `good first issue` name the files to touch and say when they're done;
`help wanted` ones are bigger. Comment on one to say you're taking it, so two people don't do the same work. Other
labels: `provider` (another AI tool), `plugin` (the Claude Code plugin), `windows`, `macos`, `needs-repro` (we
couldn't make it happen yet: steps or a Settings → Health report help most), `no-tests-needed` (a code change the
maintainer agreed needs no test).

**Replies:** we aim to answer every new issue and pull request within a few days, and sooner in the weeks after a
release.

New issues start from a template (bug, feature idea, another AI tool); questions go to Discussions, and a security
problem privately, as [SECURITY.md](../SECURITY.md) says. Pull requests get a short checklist. Everyone follows the
[code of conduct](../CODE_OF_CONDUCT.md).

**Branches:** `main` holds released code only. All work goes into `develop` first, and `develop` is merged into
`main` when a release is cut. Only the maintainer opens pull requests into `main`; one opened there by anyone
else is moved to `develop` automatically. Nobody but the maintainer can push to `main` or `develop` — you work
in your own fork and send a pull request.

1. Fork the repository, then branch off the latest `develop`:
   ```bash
   git clone https://github.com/<you>/stickyAIusage.git
   cd stickyAIusage
   git remote add upstream https://github.com/mohammad-k-shah/stickyAIusage.git
   git fetch upstream
   git checkout -b feature/my-change upstream/develop   # or fix/short-description
   ```
2. Make your change, and add or adjust tests: `dotnet test tests/StickyAIusage.Tests` must pass. A pull request
   that changes code under `src/` without touching `tests/` fails the **Unit tests included** check, which
   comments with the files that need tests. Docs, images and comment-only edits don't need tests.
3. Commit and push your branch to your fork: `git push -u origin feature/my-change`
4. Open a pull request with **base: `develop`** (not `main`), describing what changed and why
5. For UI changes, check the **ui-screenshots** / **mac-ui-screenshots** artifacts on your pull request's CI run

Your GitHub handle is listed under **Contributors** on the release that ships your change.

**Good places to start:** a [new session-card field](#add-a-session-card-field) (one entry in Core and it appears in
both apps), a [price-list update](#update-the-price-list) (JSON only), a [translation](#translate-the-readme), or
the Linux build nobody has written yet.

### Add a session-card field

A field is a labelled chip on a session card ("Model: Opus 5"); the customizer lists every one. A new field is one
entry in Core, and both apps pick it up:

1. In [`src/StickyAIusage.Core/ViewModels/SessionFields.cs`](../src/StickyAIusage.Core/ViewModels/SessionFields.cs),
   add an `Add(...)` line in the category it belongs to: a short id (lowercase with hyphens; it is stored in
   `settings.json`, so never rename one later), the chip's name, the `FieldCategory`, a one-line description (the
   customizer shows it) and a function from `SessionInfo` (`src/StickyAIusage.Core/Models/SessionInfo.cs`) to the
   text, returning `null` when the session has nothing to show (the chip is then left out). Format numbers with the
   helpers in `ViewModels/Format.cs` (`Format.Duration`, `Format.SessionCost`, …) so they read like the others.
   Example, a "Cost per hour" field: `Add("cost-per-hour", "Per hour", FieldCategory.Session, "Cost divided by the
   time the session ran", s => …)`, using the session's cost (`ExactCostUsd`, else `EstimatedCostUsd`) and
   `Elapsed`, and `null` under a minute.
2. Make sure the customizer's sample session (`ViewModels/CardPreview.cs`, `CardPreview.Sample`) gives it a value:
   `Every_catalog_field_has_something_to_show_in_the_preview` fails otherwise.
3. Add a unit test in `tests/StickyAIusage.Tests/CardLayoutTests.cs` with a `SessionInfo` that has a value and one
   that has none: `SessionFields.Find("cost-per-hour")!.Value(session)` gives the chip's text (`SessionCostTests`
   does the same for `cost`). `SessionInfo` is in the `StickyAIusage.Models` namespace: add that `using` at the top
   of the file. A `SessionInfo` needs `SessionId`, `TranscriptPath` and `WorkingDirectory`; set `StartedAt` and
   `LastActivityAt` for anything that depends on how long it ran.
4. The README says how many fields there are ("49 fields" in three places); `ReadmeLinkTests` checks the number
   against the catalog, so update it.
5. Run `dotnet test tests/StickyAIusage.Tests`. Both UI test projects render every field in the customizer, so CI
   draws yours too.

### Translate the README

`README.md` is the reference. `README.zh-CN.md`, `README.ja.md` and `README.ko.md` translate its top sections,
"Everything it does" and the FAQ, and name the English version they follow. A review by a native speaker is the most useful pull request: fix what
reads like a machine, keep the links and pictures as they are, and update the version line. A new language is a new
`README.<code>.md` with the same parts, plus a link to it in every README's language line (`ReadmeLinkTests` checks
that each README links all the others, and every link and picture). The app's own text isn't translatable yet; that
is on the [roadmap](ROADMAP.md).

### Update the price list

Model prices and context windows live in one file, [`data/models.json`](../data/models.json): the app embeds it,
and with **Check for updates** on, installed copies fetch it from `main` once a day. A new model or a price change
is a JSON-only pull request:

1. Check [Anthropic's pricing page](https://platform.claude.com/docs/en/about-claude/pricing) (and the
   [models overview](https://platform.claude.com/docs/en/about-claude/models/overview) for context windows).
2. Add or change the row. Rows are tried top-down; the first whose `match` (a case-insensitive regular expression
   found in the model id, e.g. `opus`) fits and whose `minVersion`–`maxVersion` holds the id's version wins
   (`claude-opus-5-5` is version 5.5). Give the newest version of a family its own row with `maxVersion` set, so a
   model newer than every row shows its cost with "≈" until the list knows it. Prices are US dollars per million
   tokens: `input`, `output`, `cacheRead` (left out: 0.1 × input); cache writes follow `rules` unless the row sets
   `cacheWrite5m` / `cacheWrite1h`; `fastMultiplier` for fast mode; `longPrompt` for prices above a prompt size.
3. Set `updated` to the day you checked: a downloaded list replaces the built-in one only when its date is newer.
4. Add the model to `Every_listed_model_has_its_published_prices_and_context_window` in
   `tests/StickyAIusage.Tests/ModelCatalogTests.cs` with the prices from the page (one `InlineData` line: the model
   id, input, output, cache read, 5-minute and 1-hour cache write per million tokens, and the context window), and
   run `dotnet test tests/StickyAIusage.Tests`: the file is validated (schema, prices between 0 and 1,000, no two rows
   claiming the same versions). If your row is a family's new newest version (a Sonnet newer than every Sonnet row),
   `A_newer_version_of_a_listed_family_gets_its_newest_row_and_a_rough_cost` in the same file names the row a still
   newer, unknown model falls back to: change that family's line to your row's `name`.

## The code

Two thin apps over one shared, tested core, all **.NET 10** — building them needs the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). [ARCHITECTURE.md](ARCHITECTURE.md) explains how the
parts fit; the map:

```text
src/
├── StickyAIusage.Core/      shared: usage sources + plan detection, the sign-in reader (read-only: file / Keychain),
│                            transcript index (watches the projects folder) + reader, git, pricing,
│                            pace forecast (Forecasting/), alerts and their planner (Alerts/), view models,
│                            field catalog + card layout; session lights (SessionActivity); share cards
│                            (Sharing/: what a card says, and the copy/save/post flow; the year card's
│                            December–January gate); daily stats for the year card (Stats/);
│                            first run and Health (Health/); where the Windows pill docks (DockPlacement:
│                            in the taskbar or above it, pure over rectangles) and when it steps aside
│                            for full screen (FullScreenRule);
│                            AppServices builds the object graph both apps use
├── StickyAIusage/           Windows app (WPF): taskbar widget (Native/TaskbarDock: monitors, taskbars,
│                            app buttons through UI Automation, event hooks, TaskbarCreated; app.manifest:
│                            per-monitor DPI), tray icon (+ alert toasts), settings, customizer
└── StickyAIusage.Mac/       macOS app (Avalonia): menu bar widget, menu bar icon, settings, customizer
plugin/                      the Claude Code companion plugin (JavaScript hooks module, no dependencies):
                             hooks/register.js the hooks (feed, /pace, usage_status, band, note, session state,
                             budget guard); feed.js, status.js, pace.js, state.js (the session-state reducer),
                             guard.js (the budget guard's decisions) pure helpers with
                             node tests beside them; tests/ engine tests
.claude-plugin/              marketplace.json: makes this repository the plugin's marketplace
data/models.json             model prices and context windows (embedded in the app; fetched daily if enabled)
tests/
├── StickyAIusage.Tests/         xunit, any OS: core logic, plan detection, card layout, Keychain format
├── vectors/pace/                the pace forecast's definition as data: inputs and expected outputs, shared
│                                with the plugin's JavaScript tests (plugin/hooks/pace.test.mjs runs every one)
├── StickyAIusage.UiTests/       Windows: renders every WPF screen to PNG
└── StickyAIusage.Mac.UiTests/   any OS: renders every macOS screen to PNG (Avalonia headless + Skia)
scripts/build-mac-app.sh         self-contained, ad-hoc signed "Sticky AI Usage.app"
scripts/sign-mac-app.sh          its signing: ad-hoc, or Developer ID + notarization with the release secrets
scripts/install.ps1, install.sh  the one-line installers (Windows, macOS): download, check, install, remove
packaging/                       package-manager manifest templates and how each install channel is updated
```

```bash
dotnet build StickyAIusage.sln -p:EnableWindowsTargeting=true   # the flag is only needed off Windows
dotnet test tests/StickyAIusage.Tests
dotnet test tests/StickyAIusage.Mac.UiTests   # set UI_SCREENSHOT_DIR to keep the PNGs
dotnet test tests/StickyAIusage.UiTests       # Windows only
node --test plugin/hooks/*.test.mjs           # the plugin's helpers and the pace vectors (Node 20 or later)
claude plugin validate --strict ./plugin      # what the plugin hooks and calls, as Claude Code reads it
claude plugin test ./plugin                   # the hooks module against Claude Code itself (plugin/tests/*.test.ts)
```

The build treats every warning as an error (`Directory.Build.props`), so fix a warning where it comes from
rather than switching it off.

**Run your build next to the one you use every day** with a profile. It gets its own data folder
(`StickyAIusage-dev` beside the usual one), its own single-instance lock and its own launch-at-login entry, so
the installed widget's settings, cache and log are never touched:

```bash
dotnet run --project src/StickyAIusage/StickyAIusage.csproj -- --profile dev
```

On macOS pass the same argument (`open "artifacts/mac/Sticky AI Usage.app" --args --profile dev`), or set
`STICKYAIUSAGE_PROFILE=dev`. `STICKYAIUSAGE_DATA_DIR=<full path>` puts the data folder anywhere (the test
projects use it, so a test run never touches your real one). Profile names are 1–32 letters, digits, `-` or
`_`. Each copy polls the usage endpoint on its own, so two copies make twice the requests against a
rate-limited endpoint: pause the dev copy (the pause button in the flyout) when you aren't looking at it, or
keep it on **Plugin** (Settings → Usage source; a new profile starts there) so it reads only the plugin's feed.
`STICKYAIUSAGE_FEED_DIR=<full path>` points the widget at another feed folder; the plugin honors the same
variable, so a Claude Code started with it (`claude --plugin-dir ./plugin`) feeds that copy alone.

**No subscription? Run the demo.** `--demo` (Windows: `dotnet run --project src/StickyAIusage/StickyAIusage.csproj -- --demo`;
macOS: `--args --demo`) plays a scripted two minutes from `Demo/DemoData` in Core: the 5-hour window from 62% to 91% with
a pace forecast, three sessions (working → needs you → done), Codex beside Claude, and the real alerts on top; `--demo
cap` plays a monthly cap instead. `AppServices.CreateDemo` builds the graph from it: nothing of Claude Code's, Codex's
or git's is read and nothing is sent. It runs in a `StickyAIusage-demo-<random>` profile under the temp folder (so it
starts beside your installed copy and skips the first-start window), removed when it exits. Every name in it is made
up; `DemoDataTests` checks that none comes from your computer.

**README pictures are drawn, not captured.** `pwsh scripts/refresh-screenshots.ps1` runs both screenshot projects with
`UI_MARKETING_DIR` set and copies the result over `docs/images`: the macOS screens, the share cards,
`social-preview.png` (1280 × 640, for the repository's Settings → Social preview) and `demo.gif` from the Avalonia
headless tests (`MarketingTests`, any OS), the Windows screens from the WPF tests (`MarketingRenders`). WPF draws
nothing while the Windows session is locked; the tests then refuse to hand over blank pictures, and `-SkipWindows`
refreshes the rest. The list of pictures is `tests/Shared/ReadmePictures.cs`; `ReadmePictureTests` holds the READMEs
to it (only those pictures, all of them used, each with a `width`, since they are drawn at twice their size). To show
something new in the README, add a scene there and its name to the list.

**Working on the plugin** (`plugin/`, plain JavaScript, no dependencies, no build step). It needs Claude Code 2.1.287
or later. Keep decisions in the pure helpers (`feed.js`, `status.js`, `pace.js`, `state.js`, `guard.js`) with a
`*.test.mjs` beside each (`node --test plugin/hooks/*.test.mjs`, Node 20 or later), and `register.js` thin: it only
wires them to Claude Code. Engine tests that run the module against Claude Code itself live in `plugin/tests/`
(`claude plugin test ./plugin`), not beside the helpers, because `node --test` on the folder would pick them up.
`claude plugin validate --strict ./plugin` prints every hook and call the plugin uses; paste its output into
`plugin/README.md` when it changes, and keep it free of `$.http`, `$.process` and `$.model` (the plugin promises no
network, no commands and no model calls). Claude Code's own type declarations for your installed version are the
reference for every `$.…` call; where a doc and the types disagree, the types win. Try a change without installing
it: `claude --plugin-dir ./plugin`, with `STICKYAIUSAGE_FEED_DIR` pointing at the feed folder of a dev copy of the
widget so your everyday one isn't touched. Such a run keeps its readings in `plugins/store/sticky-usage_inline-*.json`
in Claude Code's folder; delete that file afterwards. Bump `version` in `plugin/.claude-plugin/plugin.json` (and the
`PLUGIN_VERSION` the records carry) with any change users get.

**The first-start window and Settings → Health** (`Health/` in Core: `HealthCheck` is pure over `HealthFacts`,
`HealthProbe` reads the three files it needs, `PluginInstaller` builds and reads Claude Code's install commands,
`Onboarding`/`ConnectFlow`/`ConnectSession` say what the window shows). A new profile shows the three steps; delete
its folder (`%LOCALAPPDATA%\StickyAIusage-dev`) to see them again. To try **Run it for me** without touching your
own Claude Code, start the dev copy with `CLAUDE_CONFIG_DIR` pointing at an empty folder and
`STICKYAIUSAGE_PLUGIN_MARKETPLACE` at this checkout (or a fork): the install then goes into that folder only, from
your copy of the plugin. A plugin install makes no model call. Tests never start `claude`: `IClaudeCli` has a fake.

**Check the crash handlers** in a Debug build of the Windows app: `STICKYAIUSAGE_FAULT=ui` throws on the UI thread
three seconds after start (the pill must stay and `diagnostics.log` must say "Unhandled exception on the UI
thread"), `STICKYAIUSAGE_FAULT=task` leaves a failed background task behind ("Unobserved task exception"). Release
builds drop the hook. Both apps write every unhandled error to the log (`UnhandledErrors` in Core).

**Files are written atomically** (`AtomicFile`: a temp file in the same folder, then a rename), so a reader never
sees half a file and a killed process leaves the old one. Use it for anything the app rewrites while it runs.
**git and gh** go through `GitInfoReader`: every git call gets `-c core.fsmonitor=false` (and `diff` gets
`--no-ext-diff --no-textconv`), nothing may prompt, and `gh` runs only for a GitHub origin.

Every rule that matters — plan detection, accounting, the field catalog, the layout and its colors — lives in
`StickyAIusage.Core`, so a change lands on Windows and macOS at once. CI builds on both and uploads the
rendered screens, so UI changes can be reviewed without running either app.

## Where the numbers come from

The app never calls a model. Settings → Usage source (`ClaudeSource` in `settings.json`) picks the plan-limit
sources: `feed` ("Plugin", the default for a new install: the feed alone), `auto` ("Automatic": the feed, and the
endpoint whenever the feed has nothing current — what a settings file from before format 3 keeps, and a folder
with no settings file but the endpoint's cache), `legacy` (the endpoint alone) or `off`. A change applies at the
window's next refresh: `AppServicesHost` rebuilds the sources between refreshes, so no fetch is cut off.

| What you see | Where it comes from | Trust |
|---|---|---|
| Plan limits (5-hour, weekly, a gateway's spend limit) | The companion plugin's feed: `claude-<session>.json` files in the `feed` folder, written by `plugin/hooks/register.js` from Claude Code's `$.session.usage()` after each turn | Official |
| Monthly spend / plan limits, credits (`auto` without a current feed reading, or `legacy`) | Claude's usage service (`ClaudeOAuthUsageSource`), with the sign-in Claude Code saved, read-only; [DATA-SOURCES.md](DATA-SOURCES.md) has the details | Unofficial |
| Session cost, tokens, cache, context, model, effort | Claude Code transcripts in `~/.claude/projects/**/*.jsonl` (or `$CLAUDE_CONFIG_DIR/projects`), read locally | Derived |
| Git and PR/CI | `git` and `gh` run against each session's working folder, cached for 30 s | Derived |

**The feed record** (schema 1, one file per session; the widget ignores unknown fields and skips a record of an
unknown schema with one log line): `schema`, `sessionId`, `observedAt` (when the plugin wrote it, ms),
`rateLimitsAt` (when Claude Code last reported the limits, ms: the reading's age is measured from it),
`rateLimits` (`{ kind, percentUsed, resetsAt }` exactly as Claude Code returns them; `resetsAt` is ISO text
today, and seconds or milliseconds are read too), `context`, `cost`, `state`, `stateSince`, `surface`, `cwd`,
`model`, `claudeCodeVersion`, `pluginVersion`. The newest `rateLimitsAt` across sessions wins (the limits are
the account's). Records older than seven days, or ended more than a day ago, are deleted by the widget.

**Adding a data source or a provider.** Account usage reaches the windows through `IUsageProvider`.
Claude's provider, `ClaudeUsageProvider`, chooses between `IClaudeUsageSource`s — `ClaudeFeedUsageSource` (the
plugin's feed) and `ClaudeOAuthUsageSource` (the endpoint above). Each source has a trust level (`Official` for documented Claude
Code interfaces, `Derived` for local files, `Unofficial` for undocumented endpoints). The provider asks the most
trusted first and doesn't ask a less trusted one at all while a better one has a fresh reading (under 60 s
while Claude Code is working, under 10 minutes otherwise); of what it collects, the newest non-stale reading
wins, and within two minutes the more trusted one; a reading with numbers beats an empty one that only says
why it has none. A new source is one class plus one line in `AppServices.Create`; its constructor takes everything it needs (HTTP client, clock, paths), so its tests
use fakes — see `ClaudeOAuthUsageSourceTests`. Features that work for any provider read `ProviderUsage` (a list
of windows, a spend cap, the source and its trust) rather than the Claude-shaped `UsageSnapshot`.

**Another tool beside Claude** (Codex) implements `IProviderUsageReader` (`IsPresent`, `GetProviderUsageAsync`) and is
listed in `AppServices.CreateDefault`; `UsageFeed` reads it on each refresh when it is present and not in
`DisabledProviders`, runs its pace and alerts, and `WidgetViewModel` turns it into a flyout card (`ProviderSections`)
and, near a limit, a pill chip (`ExtraChips`). [`PROVIDERS.md`](PROVIDERS.md) is the guide, with `CodexUsageProvider`
(Codex's session logs) as the worked example.

**Session accounting** lives in `ClaudeSessionLogReader` (each response counted once, a forked conversation and its
subagents kept apart, empty sessions hidden, only new bytes read on each refresh), and its tests pin every rule:
change one only with a test that shows why. Costs are estimates from the price list (`data/models.json`, read by
`ModelCatalog`); a model the list doesn't know shows "≈", and with the plugin a session shows Claude Code's own
figure instead (`SessionCosts`). Anthropic's bill is authoritative.

**Polling and sign-in** (the endpoint source only; the feed is read on every 15-second tick and at once when
a record changes): 30 s while a transcript changed in the last 2 minutes, 10 min otherwise, plus one
check as soon as a 5-hour or weekly window resets. A `429` backs off for the server's `Retry-After` and the
cooldown survives a restart. The sign-in (a file on Windows, a Keychain item on macOS) is only read, once per
request, and never renewed or written: while its token has expired, the source asks nothing and waits until Claude
Code renews it. None of this runs in the "Plugin" or "Off" modes: the endpoint source isn't even built there
(`AppServices.Create`).

**The card layout** is one readable line in `settings.json` —
`"CardFields": "model,effort|in,out,cached,total|cache-hit,cache-read,cache-write"`, one row per `|`. Colors
aren't stored: a chip's color is its position in its row, which is why a drop recolors it instantly. Unknown
field ids are ignored rather than breaking the file.

## Releasing

Versions follow `vMAJOR.MINOR.PATCH`:

| Bump | When | Example |
|---|---|---|
| MAJOR | Big, impactful change or a new feature area (a new platform, a new provider) | `v1.4.2` → `v2.0.0` |
| MINOR | Smaller features and improvements | `v1.4.2` → `v1.5.0` |
| PATCH | Bug fixes only | `v1.4.2` → `v1.4.3` |

To release, merge `develop` into `main`, then push a tag from `main`. The **Release** workflow builds both apps
and publishes them on the Releases page:

```bash
git checkout main
git merge --no-ff develop
git push origin main
git tag v1.1.0
git push origin v1.1.0
```

The release notes list every merged pull request grouped by its label (see `.github/release.yml`), and GitHub
shows each pull request's author under **Contributors**. Label pull requests `feature`, `enhancement`, `bug` or
`documentation` before merging so they land in the right group.

A tag with a hyphen (`v2.0.0-beta.1`, `v2.0.0-rc.1`) is published as a pre-release. Each release carries:

| File | What |
|---|---|
| `StickyAIusage-win-x64.zip` | self-contained single-file exe (no .NET install needed), what `scripts/install.ps1` and Scoop install |
| `StickyAIusage-win-x64-small.zip` | the same exe framework-dependent: well under 1 MB, needs the .NET 10 Desktop Runtime |
| `StickyAIusage-mac-apple-silicon.zip`, `-mac-intel.zip` | the app bundles, what `scripts/install.sh` installs |
| `checksums.txt` | SHA-256 of every zip; both install scripts refuse a download that doesn't match it |

Nothing is published that hasn't started: the Release workflow runs all three test projects, then
`--self-test` on the exact files it zips (both Windows exes; both Mac bundles, the Intel one under Rosetta), and
any failing check stops it before the release is created. In the public repository it then signs a build
provenance for every file in `checksums.txt` (`actions/attest`), which `gh attestation verify <file> --repo
mohammad-k-shah/stickyAIusage` checks. A dry run belongs in a fork, on a throwaway pre-release tag
(`v0.0.0-dryrun.1`) whose release you delete afterwards — never in the main repository.

**`--self-test [report-file]`** (both apps; `SelfTest` in Core, `SelfTestRunner` in each app) runs in a temporary
profile (its own data, feed and Claude folders, no single-instance lock), never shows a window and writes one
`PASS`/`FAIL` line per check, then exits 0 or 1 within 20 seconds. Both start with the shared checks (data folder,
resources, price list, settings, and the transcript index: its first walk, then a session written after it, which
must arrive through the folder watcher alone; and the year stats: two sample sessions read, then a second pass that
must take the finished days from the month files; the Codex reader: a sample session log of today read back
as its two windows; and Health: a sample transcript and plugin marker must read as found and connected, the
report must hide the home folder, and a data folder without settings must be a first run; and the update check: the
build's version must be a release version, and a sample release list, answered without going online, must read as
an update). Windows checks the
embedded tray icon, both palettes, the first refresh, the pill, flyout, Settings (with its Health list), the
first-start window's three steps and the upgrade screen, and the customizer built and rendered off-screen, and the share
cards (the year card too) drawn and encoded; macOS sets Avalonia up without starting it, checks both theme variants,
draws three share cards and opens every asset. A failed report ends with the
run's log. The Build workflow runs it on its published exe, on the installed copy in the `installer` job and on the
Mac bundle. A new resource, window or start-up step belongs in it: `SelfTestTests` covers the helper, and both UI
test projects run each app's own checks.

**CI:** a push that changes only `docs/` or Markdown files starts no build. The macOS job runs in the public
repository (and when the Build workflow is started by hand); the macOS screens also render headless in the Windows
job, so every push checks them. CodeQL scans the C# code on every push and weekly, and Dependabot opens one pull
request a week per ecosystem (NuGet, GitHub Actions) into `develop`. Builds in GitHub Actions are
`ContinuousIntegrationBuild`s: repository-relative paths in the binaries and Source Link to the commit.

**Signing** is wired in and stays off until the maintainer sets it up; without it a release is unsigned, as before.
*Windows:* set the repository variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`
and the secret `SIGNPATH_API_TOKEN` (SignPath's free program for open-source projects); the Release workflow then
uploads both exes for signing before the self-test, and the Setup after it is built, and refuses a file whose
Authenticode signature isn't valid. *macOS:* add the secrets `MAC_CERTIFICATE_P12` (the "Developer ID Application"
certificate with its key, base64) and `MAC_CERTIFICATE_PASSWORD`, and for notarization `MAC_NOTARY_KEY` (an App Store
Connect API key's `.p8`, base64), `MAC_NOTARY_KEY_ID` and `MAC_NOTARY_ISSUER`; `scripts/sign-mac-app.sh` then signs
every binary with the hardened runtime and `scripts/mac-entitlements.plist`, has Apple notarize the zip and staples
the ticket. `SigningHookTests` run that script with stand-ins for `codesign` and `xcrun` and check that every
signing step waits for its settings.

**Update notice.** With Check for updates on, `UpdateChecker` (Core, `Updates/`) asks GitHub's releases list once a
day and compares the tags with the running version (Semantic Versioning; pre-releases only for a pre-release build).
So the version a release is built with (`-p:Version=` from the tag) is what people are told about: never move a
published tag. The installers leave `install-method` beside the exe ("setup", "winget" through the manifest's
`/CHANNEL=winget` switch, "script") so the notice names the right update command (`InstallMethods`).

Symbols are embedded in the exe (`DebugType=embedded`), so a crash in `diagnostics.log` keeps its line numbers and
the zips hold the exe alone. The run's **package-manifests** artifact has the Scoop manifest filled in for that
release; [`packaging/README.md`](../packaging/README.md) says how each install channel is set up and updated.

**The READMEs.** `README.md` is the reference; `README.zh-CN.md`, `README.ja.md` and `README.ko.md` translate its top
sections, "Everything it does" and the FAQ, and say which English version they follow. When a feature changes those
parts, change the English first and note the translations that need the same change (or change them, if you can).
`ReadmeLinkTests` checks every relative link, picture and `#section` link in the four files (anchors as GitHub makes
them), so a renamed heading or a moved picture fails the build.

Changing a script? `InstallScriptTests` run both install scripts against a release folder they build
(PowerShell on Windows, `sh` on macOS and with Git for Windows), with every path in a temp folder. Keep the
`.ps1` files plain ASCII (Windows PowerShell reads them in the system code page) and `scripts/install.sh`
executable in git (`git update-index --chmod=+x`) with LF line ends (`.gitattributes`); tests check all three.

Windows ships self-contained (no .NET install needed to run it); macOS ships an ad-hoc signed app bundle for
Apple Silicon and Intel. The Mac app needs **macOS 15 or later**, the oldest version .NET 10 supports; the
number is `LSMinimumSystemVersion` in `scripts/build-mac-app.sh`, and the README repeats it. The **Build**
workflow's artifacts are for us — they need a GitHub sign-in and they expire — so the Releases page is what
people should be pointed at.

## Files the app writes

[DATA-SOURCES.md](DATA-SOURCES.md) is the full list of what the app and the plugin read, write and send, for users;
a test checks that every web address, environment variable and data-file name in the code appears there. The
details of each format:

`%LOCALAPPDATA%\StickyAIusage\` on Windows; `~/Library/Application Support/StickyAIusage/` on macOS, with logs
in `~/Library/Logs/StickyAIusage/`. A profile uses `StickyAIusage-<name>` in the same places.

| Path | Contents |
|---|---|
| `settings.json` | Settings, the card layout, and the widget's last position (`WidgetDocked`: Windows starts by the tray; otherwise at `WidgetRight`/`WidgetBottom` when that is still on a screen). Saved atomically; `SchemaVersion` says its format (4: a missing `Theme` means "system"), and keys a newer version wrote are kept |
| `settings.json.bak` | The previous `settings.json`, loaded if the main file can't be read |
| `app.lock`, `app.lock.signal` | macOS: the single-instance lock, and the file a second launch touches to open the running copy's flyout |
| `cache/` | `alerts.json`: which notifications were already given (`"Schema": 1`; keys `provider|kind|reset|alert kind|threshold or day`, kept a day past their window's reset; a session light's "needs you" or "finished" key is `claude-code|session:<id>|none|NeedsYou|<episode start, ms>`, kept a day past that start) and each window's peak, so a restart repeats nothing and a busy window's reset can say "you're back"; written atomically, only when it changed. `journal.jsonl`: the usage readings the pace forecast is worked out from (one JSON object per line, `"schema": 1`; a repeat of the last reading is skipped; rewritten atomically past 5,000 lines or with samples older than 8 days, 35 for spend caps). Last usage reading and rate-limit cooldown, so a restart doesn't re-query immediately, and today's spend for the day target (all written atomically). With Check for updates on: `models.json` (the last downloaded price list, used when newer than the built-in one) and `models-check.txt` (when it last asked), and `update-check.json` (`{ "schema": 1, "checkedAt", "etag", "newest", "newestIsPreRelease", "newestStable" }`: when the update check last asked GitHub, the list's ETag and the newest release tags; another schema counts as never asked). `stats/<yyyy-mm>.json` (only once Share was opened in December or January): the year card's daily rollups, `{ "schema": 1, "month", "zone", "prices", "days": { "<yyyy-mm-dd>": { "models": { "<model id>": { input, output, cacheRead, cacheWrite, costUsd, responses } }, "sessions": [ids], "hours": [24 response counts], "costIsRough" } } }`; a day is written once it has been over for an hour and never read from the transcripts again; a file of another zone, price list or schema is counted again (a newer schema's is left alone). `DailyStatsStore` and `YearStatsService` in Core; the full year-and-month recap is meant to read the same files |
| `feed/` | Written by the Claude Code plugin, read by the widget: `claude-<session>.json` per session (since plugin 2.2 also `state`: `started`, `working`, `needs_you`, `done`, `idle`, `rate_limited`, `ended`; `stateSince`; `attention`: `{ type: "permission" | "question" | "input", tool? }` while it waits; `lastTurnMs`; read by `SessionActivities`) and `plugin.json` (which plugin and Claude Code wrote last; its presence alone tells the widget the plugin is installed). Written by the widget: `forecast.json` (`{ schema, updatedAt, windows: [{ kind, percentUsed, resetsAt, hitsLimitAt, projectedAtReset, confidence }] }`, atomically, at least once a minute while readings come; the plugin's `/pace`, band and tool take its limit time while it is under two minutes old, and their own pace otherwise). Always in the default folder, for every profile |
| `diagnostics.log` | Errors and state changes, for troubleshooting. Each start writes "App started (Windows): Sticky AI Usage <version> (<commit>) · <profile> · usage source: <source> · .NET <runtime>" (the same words as the last line of Settings); every unhandled error is written here, at most ten a minute. Past 1 MB it moves to `diagnostics.old.log`; your home folder is written as `~` |

Share cards are written only where you save them (Pictures by default, `usage-<yyyyMMdd-HHmm>.png`, or there as a
fallback when the clipboard refuses an image); on macOS the clipboard copy goes through
`StickyAIusage-share-card.png` in the temp folder. Everything a card says comes from `ShareCardModel` in Core;
`ShareCardModelTests` feed it distinctive project, session, branch, repository and e-mail names and check that none
reaches any of its strings, and both UI test projects check that the rendered views draw only the model's text.

`--self-test` writes none of these: it works in a `StickyAIusage-selftest-<random>` folder under the system temp
folder, deleted when it finishes, and writes only the report file you name. `--demo` doesn't either: its profile is a
`StickyAIusage-demo-<random>` folder there, removed when it exits.

More: [changelog](CHANGELOG.md).
