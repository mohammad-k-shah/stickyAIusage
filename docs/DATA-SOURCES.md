# Data sources: everything it reads, writes and sends

This page lists every file Sticky AI Usage reads or writes, every program it starts and every network request it
makes, per source, with how far to trust each number and how to turn each one off. If the code and this page ever
disagree, that is a bug: please [open an issue](https://github.com/mohammad-k-shah/stickyAIusage/issues).
(A unit test checks that every web address, environment variable and data-file name in the code is on this page.)

**In short:** out of the box it reads Claude Code's local files and what the companion plugin writes, keeps a few
small files of its own, and **makes no network request and never reads your Claude sign-in**. Everything that
reaches the network or your sign-in is listed below and is either off by default or only happens when you click.
It never calls a model, has no telemetry, no account and no server of its own.

## Trust levels

| Level | Meaning |
|---|---|
| **Official** | A documented Claude Code interface (the plugin's view of Claude Code's own figures). |
| **Derived** | Worked out on your computer from a tool's own files. The tool may change their format. |
| **Estimate** | Calculated by the app (costs at list prices, the limit time at this pace), and labelled as such. |
| **Unofficial** | An undocumented endpoint. Only in modes you choose, and labelled there. |

## The folders

| Name below | Windows | macOS |
|---|---|---|
| *data folder* | `%LOCALAPPDATA%\StickyAIusage` | `~/Library/Application Support/StickyAIusage` |
| *log folder* | `%LOCALAPPDATA%\StickyAIusage\logs` | `~/Library/Logs/StickyAIusage` |
| *feed folder* | `%LOCALAPPDATA%\StickyAIusage\feed` | `~/Library/Application Support/StickyAIusage/feed` |
| *Claude folder* | `%USERPROFILE%\.claude` | `~/.claude` |
| *Codex folder* | `%USERPROFILE%\.codex` | `~/.codex` |

A profile (`--profile <name>` or `STICKYAIUSAGE_PROFILE`) uses `StickyAIusage-<name>` for the data and log folders;
the feed folder stays the default one for every profile. Environment variables move them:

| Variable | Effect |
|---|---|
| `STICKYAIUSAGE_PROFILE` | Same as `--profile <name>` |
| `STICKYAIUSAGE_DATA_DIR` | The data folder (its logs go into its `logs` folder) |
| `STICKYAIUSAGE_FEED_DIR` | The feed folder, for the widget and the plugin alike |
| `CLAUDE_CONFIG_DIR` | The Claude folder, as Claude Code itself uses it |
| `CODEX_HOME` | The Codex folder, as Codex itself uses it |
| `STICKYAIUSAGE_PLUGIN_MARKETPLACE` | Where **Connect → Run it for me** installs the plugin from (for developers) |
| `STICKYAIUSAGE_FAULT` | Debug builds only: throws a test error to check the crash handlers. Release builds ignore it |

## Your limits

| Source | Reads | Trust | Network | Turn it off |
|---|---|---|---|---|
| **Plugin** (Settings → Usage source → Plugin; what a new install uses) | `claude-*.json` and `plugin.json` in the feed folder | Official | None | Usage source → **Off**, or remove the plugin |
| **Automatic** (what an update from an earlier version keeps) | The plugin's files, and Claude's usage service whenever they have nothing current (below) | Official, or Unofficial when the service answered | Only while the plugin has nothing current | Choose **Plugin** |
| **Legacy** | Claude's usage service only (below) | Unofficial | Yes | Choose **Plugin** or **Off** |
| **Off** | Nothing | — | None | — |

**Claude's usage service** (Automatic and Legacy only) reads the sign-in Claude Code saved: `.credentials.json` in
the Claude folder on Windows, the *Claude Code-credentials* Keychain item on macOS (through Apple's
`/usr/bin/security`). It asks `GET https://api.anthropic.com/api/oauth/usage` with that sign-in: every 30 seconds
while a Claude Code transcript changed in the last two minutes, every ten minutes otherwise, slower after a "too many
requests" answer. The sign-in is **only read**, once per request: the app never renews it and never writes anything
of Claude's. When the token has expired it asks nothing and waits, showing the last numbers with a note, until Claude
Code renews the token (it does whenever it runs); a token the service refused is not sent again.
It keeps its last answer and its back-off state in
`cache/claude-usage.json` and `cache/claude-usage-throttle.json`. This service is not documented for other apps.
In Plugin mode and Off none of this runs: the client that would ask the service is never built.

The plugin's files are read in **every** mode, Off included, for the session lights and Claude Code's own session
costs; Usage source only decides whether limits come from them. To stop them, remove the plugin.

## Sessions, costs and git

| What | Reads | Trust | Turn it off |
|---|---|---|---|
| Today's sessions: tokens, cost, context, model, skills | Claude Code's transcripts, `projects/**/*.jsonl` in the Claude folder: listed once at start, then watched (a full look every ten minutes as a safety net); only bytes added since the last read | Derived; costs are an Estimate at the list prices in [`data/models.json`](../data/models.json) | No switch: they are read on your computer and never leave it. Settings → **Current session cost** hides the pill's cost |
| Claude Code's own session cost | The plugin's record for that session | Official | Remove the plugin |
| Session lights (working, needs you, done) | The plugin's records: the session's state and, while it waits, the kind of wait and a tool's name | Official | Settings → **Session lights** |
| Git branch, changes, ahead/behind | `git status`, `diff --numstat`, `rev-parse`, `remote -v` in each listed session's working folder, read-only, with the repository's monitor hook, external diff and text filters off, no prompts; cached 30 seconds | Derived | No switch: nothing is written or sent. Settings → **Fields on each card** decides which git fields a card shows |
| Pull request and CI | `gh pr view` in that folder, only when the GitHub CLI is installed and signed in and the `origin` is on GitHub; at most every five minutes per folder | Derived (GitHub's answer) | Sign the GitHub CLI out (`gh auth logout`) or uninstall it |

**Network:** none from the app for sessions. `gh` talks to GitHub with your own GitHub CLI sign-in.

## Codex (if you use it)

Reads the end of Codex's newest session logs, `sessions/<year>/<month>/<day>/rollout-*.jsonl` in the Codex folder
(the newest three day folders, up to eight when those have no limits; the last 256 KB of a file, more only when no
reading is there), for the limits Codex wrote after its last answer. Nothing else in the Codex folder: never
`auth.json` or Codex's settings. **Derived**, no network. Without a Codex folder nothing is read or shown. Turn it
off: Settings → **Codex**.

## The pace forecast and alerts

Worked out on your computer from the readings above (**Estimate**). Writes `cache/journal.jsonl` (the readings it
saw: the last eight days, 35 for a monthly cap) and `forecast.json` in the feed folder (the limit time, for the
plugin). `cache/alerts.json` remembers which notifications were given, so a restart repeats none. Notifications:
Windows shows them from the tray icon; macOS runs `/usr/bin/osascript` with the notification's text. Turn them off:
Settings → **Pace forecast** and Settings → **Usage alerts**.

## Update check and price list updates (off by default)

Settings → **Check for updates** is the one switch for both. While it is off, the app makes no network request of
its own. With it on, once a day (never in the first minute after a start):

- **New versions:** one request to `https://api.github.com/repos/mohammad-k-shah/stickyAIusage/releases?per_page=10`
  (GitHub's public list of this project's releases; no sign-in). It sends the app's name as the User-Agent and,
  from the second day on, the list's ETag, so an unchanged list is a short "not modified" answer; nothing about you,
  your computer or your sessions. The answer (the newest version tags and the ETag) is kept in
  `cache/update-check.json`. A newer version is **only shown** - a line at the foot of the flyout, an item at the top
  of the tray / menu bar menu and Settings → Health - with its release page and the command that updates your copy;
  the app never downloads or installs anything. Pre-releases count only while you run one. A refused or failed
  check changes nothing and is tried again a day later.
- **Prices:** it downloads `https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/data/models.json`
  (at most 256 KB, validated), keeps it as `cache/models.json` when it is newer than the built-in list, and notes the
  time in `cache/models-check.txt`.

To name the right update command, the Windows app reads the one-word `install-method` file its installer left beside
it (below), or sees that it runs from Scoop's folder. The Mac app reads nothing for this: the install script updates
any copy in Applications.

## Connecting the plugin, and Settings → Health

- **Health** reads the version line of your newest Claude Code transcript, `plugin.json` in the feed folder and
  `plugins/installed_plugins.json` in the Claude folder (only whether the plugin is listed). It starts nothing and
  sends nothing. **Copy report** puts the checks on your clipboard with your home folder written as `~`.
- **Connect → Run it for me** runs only when you click it, after showing the command: Claude Code's own
  `claude --version`, then `claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage` (or the
  two-step form an older Claude Code needs). To find `claude` it looks on your `PATH` and where Claude Code's
  installers put it. Claude Code then downloads the plugin from GitHub and records it in its own settings, as when
  you type the command. No model is called.
- **Sign in** (Windows, Automatic and Legacy, when your sign-in expired) opens a terminal with `claude auth login`
  when you click it.
- Links in Health and Settings (`https://code.claude.com/docs/en/setup`,
  `https://github.com/mohammad-k-shah/stickyAIusage/releases`) open in your browser when you click them.

## Sharing

Share cards are drawn on your computer and go only where you put them: the clipboard, a file you save (Pictures,
`usage-<date>-<time>.png`, or wherever you choose), or a post. **Post on X / LinkedIn / Bluesky** copies the image and
opens `https://x.com/intent/tweet?text=…`, `https://www.linkedin.com/sharing/share-offsite/?url=…` or
`https://bsky.app/intent/compose?text=…` in your browser with a fixed line of text and
`https://github.com/mohammad-k-shah/stickyAIusage`; nothing about you or your usage is in the address. On macOS the
clipboard copy goes through `StickyAIusage-share-card.png` in the temp folder and `/usr/bin/osascript`. The year card
(December and January) keeps daily totals in `cache/stats/<year>-<month>.json`: tokens and estimated cost per model,
session ids, answers per hour; no project names and no prompts.

## The widget's own files

All in the data folder unless named otherwise; all written atomically (a temp file, then a rename).

| File | What | Kept |
|---|---|---|
| `settings.json`, `settings.json.bak` | Your settings and the previous copy | Until you delete them |
| `cache/day-spend.json` | Today's spend for the monthly cap's day line | Today |
| `cache/claude-usage.json`, `cache/claude-usage-throttle.json` | Automatic and Legacy only (above) | Overwritten |
| `cache/journal.jsonl`, `cache/alerts.json` | The pace and alerts (above) | 8 or 35 days; a day after a reset |
| `cache/models.json`, `cache/models-check.txt` | Price list updates (above) | Overwritten |
| `cache/update-check.json` | The update check (above): when it last asked, the ETag, the newest versions | Overwritten |
| `cache/stats/<year>-<month>.json` | The year card (above) | Until you delete them |
| `forecast.json` in the feed folder | The pace, for the plugin | Overwritten |
| `diagnostics.log`, `diagnostics.old.log` in the log folder | Errors and state changes, your home folder written as `~` | 1 MB, then one older file |
| `app.lock`, `app.lock.signal` | macOS: one copy per profile, and "open the running copy" | While it runs |

The widget also deletes plugin records older than seven days (one day after their session ended) from the feed
folder.

**Start at login** (when it is on: the first-run window offers it switched on and applies it when you click Finish;
Settings → **Launch at login** / **Start with Windows** turns it on or off): Windows writes the value
`StickyAIusage` (`StickyAIusage-<profile>` for a profile) under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
macOS writes `~/Library/LaunchAgents/com.stickyaiusage.widget.plist` (`com.stickyaiusage.widget-<profile>.plist` for a
profile). Turning it
off removes it.

**Docking in the Windows taskbar** reads where the taskbar, the tray and the app buttons are (positions only,
through Windows' window and UI Automation interfaces) and whether a full-screen app or a presentation is running.
Nothing is stored or sent. **Open log folder** opens the log folder in Explorer or Finder.

## The plugin

The companion plugin runs inside Claude Code and its permissions are what Claude Code itself reports
(`claude plugin validate --strict ./plugin`, listed in [plugin/README.md](../plugin/README.md)): **no network, no
commands, no model calls.**

| It writes | What | Turn it off |
|---|---|---|
| `claude-<session>.json` in the feed folder | Per session: the limits Claude Code reported and when, context, Claude Code's cost, the model, the working folder, the surface (terminal, desktop, VS Code), the Claude Code and plugin versions, and the session's state (the kind of wait and a tool's name; never your prompt, a command or a file name) | `/config` → `feed` off (the files stop; `/pace` keeps working) |
| `plugin.json` in the feed folder | Which plugin and Claude Code versions wrote last | Same |
| Its store in Claude Code's folder (`plugins/store/sticky-usage*.json`) | The last 200 readings per window, for `/pace` without the widget | Remove the plugin and delete the file |

It reads `forecast.json` from the feed folder, and the environment variables `HOME`, `USERPROFILE`, `LOCALAPPDATA`,
`XDG_DATA_HOME` and `STICKYAIUSAGE_FEED_DIR` to find that folder (on Linux, `$XDG_DATA_HOME/StickyAIusage/feed`,
default `~/.local/share/StickyAIusage/feed`). Remove it with `/plugin` → uninstall `sticky-usage`.

## Demo mode and the self-test

`--demo` and `--self-test` read nothing of yours: no transcripts, plugin files, sign-in, Codex logs or git. Each
works in its own temporary folder (`StickyAIusage-demo-<random>`, `StickyAIusage-selftest-<random>` under the
system temp folder; the self-test writes a sample Codex log there, `rollout-selftest.jsonl`) and deletes it when it
exits. `--self-test <file>` also writes the report to the file you name. Neither sends anything.

## The installers (only when you run one)

- **`scripts/install.ps1` and `scripts/install.sh`** ask `https://api.github.com` which release is the newest and
  download its zip and `checksums.txt` from `github.com`; they refuse a file whose SHA-256 doesn't match. They
  install into `%LOCALAPPDATA%\Programs\Sticky AI Usage` (Windows, with a Start Menu shortcut) or Applications
  (macOS) and keep a list of the files they installed (`installed-files.txt`) so uninstalling removes exactly
  those. `install.ps1` also writes `install-method` ("script") beside the exe, for the update notice.
  Uninstalling also removes this copy's start-at-login entry; `-Purge` / `--purge` deletes the data folder.
- **`StickyAIusage-Setup-x64.exe`** (Windows) installs into the same folder with a Start Menu shortcut and an entry
  in Settings → Apps, and writes `install-method` ("setup", or "winget" when winget runs it); removing it removes that
  file too and asks before deleting your settings.
- Neither changes anything of Claude Code's.
