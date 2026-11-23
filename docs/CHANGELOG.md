# Changelog

What changed and why. Tagged versions are published on the
[Releases page](https://github.com/mohammad-k-shah/stickyAIusage/releases). **v1.0.0** is the first public
release and includes everything from the 2026-09-22 section down. **v2.0.0** includes everything from the
2026-11-14 section up; its highlights come first, and the dated sections record how it was built up.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely: grouped by what it means
for someone using the widget, not by file.

---

## v2.0.0 — the highlights

- **Your limits straight from Claude Code.** A small companion plugin hands Claude Code's own 5-hour and weekly
  numbers to the widget after each answer: no sign-in token, no network request, no undocumented service. New
  installs use it; Settings → Usage source keeps the older route as an opt-in that only reads your sign-in.
- **Know before you hit the limit.** The pace forecast ("5h: 88% - limit ~1:34 PM"), alerts before you're blocked
  and when a window resets, and the same numbers inside Claude Code: `/pace`, a band above the prompt, a usage tool
  for Claude itself, and a budget guard that asks before subagents use up the rest.
- **Which session needs you.** Session lights on the pill and the cards: working, waiting for your permission or
  answer, done.
- **Codex beside Claude**, read from Codex's own session logs.
- **Share cards** of today's usage, one session or (in December and January) your year with Claude Code.
- **Windows:** docked inside the taskbar, a per-user installer, one-line install scripts with checksums, a first-run
  window and Settings → Health. **macOS 15 or later**, on .NET 10.
- **Trust:** every download self-tested and attested before it is published, prices as data, a page listing
  everything it reads, writes and sends, an optional daily update notice, and no telemetry.

---

## 2026-11-23 — final polish

### Fixed

- **macOS:** the breathing session-light dot no longer fills the log with "Looping animations must not use the Run
  method" errors whenever new numbers arrive or the panel opens.
- **Settings → Save no longer undoes a change made elsewhere** while the window is open: turning Launch at login (Start
  with Windows) on or off from Health or the menu bar/tray item now sticks.
- **The log no longer names your account** through Claude Code's project folder names (`-Users-<name>-…`): like the
  home folder itself, that part is written as `~`. The plugin-install error in the welcome window does the same.
- **Demo mode** removes its temporary folder however the app is quit, Cmd-Q and AppleScript `quit` included.
- **A tool you haven't used for two weeks** (an old Codex session) no longer gets a card with long-passed resets; it
  comes back with the tool's next use. Long ages read "290 days ago" instead of "290d 17h ago".
- **`/pace` in a brand-new session** shows the newest numbers another session saw, marked "(as of …)", instead of "No
  plan limits reported for this session" (plugin 2.3.1).
- A folder such as `StickyAIusage` reads "Sticky AIusage" on its card, not "Sticky A Iusage".
- The pill's tooltip no longer covers the open panel's header.
- The share card's temporary image is deleted once it is on the clipboard (macOS).
- The Windows install script says where to find the widget: in the taskbar, left of the tray icons.
- `install.sh --help` prints its help also when the script is piped from `curl`.

### Changed

- The welcome window says everything the plugin does: besides your limits, it gives Claude a usage tool and, near a
  limit, a one-line usage note (each can be turned off). Step 2 says which line goes in a terminal and which into Claude
  Code.
- Settings hides the Day target on plans with rolling limits (it only applies to a monthly cap) instead of showing it
  greyed out, and Usage source stops offering the plugin's install command once the plugin is connected.
- The Mac self-test names the Mac's update command.
- The README's moving picture shows more of the app, each part with a caption: the panel's pace lines and sessions,
  Codex beside Claude, a monthly cap, the share cards and the year card, the card customizer and the light theme.
- The Chinese, Japanese and Korean READMEs list everything the app does.
- Each release page starts with what's new and the moving picture.

---

## 2026-11-21 — know when a new version is out

### Added for contributors

- Issue templates for a bug (asking for the Settings → Health report), a feature idea and another AI tool to support;
  a pull-request checklist; a [security policy](../SECURITY.md) with private reporting; a [code of conduct](../CODE_OF_CONDUCT.md).

### Added

- **Update notice:** with Settings → **Check for updates** on, the app asks GitHub once a day whether a newer version
  is out. When one is, a line at the foot of the panel says "Update available: v2.0.1 — what's new" (it opens the
  release page) with the command that updates your copy — `winget upgrade …`, `scoop update …`, the install
  script's line, or "download it from the release page" — and a Copy button; the tray / menu bar menu and
  Settings → Health say the same. Nothing is downloaded or installed for you. Beta builds hear about the next beta;
  everyone else only about full releases. The check sends only the app's name, waits a minute after start, and a
  failed or refused one changes nothing and tries again a day later. **Check for updates** stays off unless you
  turn it on; it also keeps fetching the latest model prices.
- The Windows installer and the install script note how the app was installed (a one-word `install-method` file
  next to it), so the notice names the right command.

### Changed

- **The app never changes your Claude sign-in any more.** Automatic and Legacy used to renew Claude Code's sign-in
  token shortly before it expired and save the new one back; now they only read it. When it has expired, the panel
  keeps the last numbers and says to start Claude Code once (Claude Code renews its own token whenever it runs), or
  to use the plugin, which needs no token at all. The app asks the usage service only with a token that is still
  good, and reads the sign-in once per request.
- Releases are ready to be signed: the Windows exes and the Setup through SignPath, and the Mac app with a Developer
  ID and Apple's notarization. Until the accounts behind them are set up, downloads are unsigned exactly as before.

---

## 2026-11-21 — what it touches, in one place; and a way in for contributors

### Added

- **[Data sources](DATA-SOURCES.md):** every file the app and its plugin read or write, every program it starts and
  every web address it can ask, per source, with how far to trust each number and how to turn each one off. A test
  checks that every web address, environment variable and data file in the code is listed there.
- **[Architecture](ARCHITECTURE.md)** on one page: the three projects, where usage comes from and how far to trust
  it, how the plugin and the widget share a folder, where each kind of logic lives, how the tests draw both apps.
- **[Roadmap](ROADMAP.md):** what's being built now, what comes next and what's further out.
- **Contributor guide, refreshed:** step-by-step recipes for a new session-card field and a price-list update,
  translating the README, working on the plugin, the labels and how soon you hear back. The provider guide got a
  step-by-step list and the rule that anything undocumented stays opt-in and labelled.

### Changed

- The README's privacy section and the translations point at the full data-sources page; "What's next" points at the
  roadmap and no longer lists Codex as coming (it's here).
- The price-list tests no longer depend on the list's date, so a price update is a JSON change and one test line.

---

## 2026-11-20 — see it move, without an account

### Added

- **Demo mode:** start the app with `--demo` (or `--demo cap` for a monthly spend cap) and it plays two minutes of
  made-up usage: the 5-hour window climbs from 62% to 91%, the limit time at this pace turns yellow, orange and red,
  three sessions go from working to "needs you" and done, a Codex card sits beside Claude's, and the alerts fire as they
  would. It runs in a temporary profile next to your own copy, reads nothing of Claude Code's, Codex's or git's, and
  leaves nothing behind when it exits. Good for trying it before you connect anything, and for screenshots.
- **A moving picture at the top of the README.** The README's pictures are drawn by the app's own screenshot tests from
  the demo, at twice the size so they are sharp on high-density screens, and one script
  (`scripts/refresh-screenshots.ps1`) redraws them all. A social preview picture for links to the repository.

### Changed

- The sample session in the field customizer's preview now lives in the made-up `acme-webapp` folder, like the demo.

---

## 2026-11-20 — a front page, in four languages

### Added

- **The README is a front page now:** what it does in one screen (the tagline, a picture, the one-line install), then
  three things it's for (knowing before you hit the limit, knowing which agent needs you, letting the agent know too),
  how it gets its numbers (a diagram and a table: where each number comes from, how far to trust it, and that none of it
  leaves your computer), and a FAQ ("why not just the status line?"). Everything that was there before is still below.
- **README in Simplified Chinese, Japanese and Korean** (the top of the page and the FAQ), each linking the others and
  saying which English version it follows. Corrections from native speakers are welcome.
- A test checks that every link, picture and section link in the four READMEs leads somewhere.

---

## 2026-11-20 — in the taskbar itself

### Added

- **The Windows widget now sits inside the taskbar**, just left of the tray icons, centered and sized to the taskbar,
  like a system indicator. When the taskbar has no room beside your app buttons it waits just above the tray instead,
  and moves back in when you close windows. A taskbar that hides itself keeps it above the screen edge.
- **Pick the monitor**: with several monitors, Settings → **Widget position** (or the tray menu's **Move widget to**)
  chooses whose taskbar it docks on; its panel opens on that monitor and sizes itself to it.
- **Out of the way in full screen**: while a full-screen app, a film or a presentation runs on its monitor, the widget
  steps aside, and comes back afterwards (Settings → **Hide in full screen**).
- The first-start window asks where the widget should sit (in the taskbar or above it).

### Changed

- Updating from an earlier version: a widget at its usual spot moves into the taskbar; one you dragged somewhere stays
  there. **Reset widget position** docks a dragged widget again.
- It no longer checks every second whether the taskbar has covered it: Windows tells it when the foreground or the
  taskbar changes (and Explorer restarts are noticed), with a light check every five seconds. Idle CPU went down.
- Sharp at every monitor's own scaling (per-monitor DPI awareness).

---

## 2026-11-20 — up in a minute: first start and Health

### Added

- **A first-start walkthrough.** A new install opens a short window: what the app reads (and the one network request
  you can turn on), **Connect to Claude Code**, and **Make it yours** (start at login, notifications). Connect shows the
  install command for the plugin and either runs it for you (**Run it for me**, when Claude Code's command line is on
  your computer) or copies the line to paste into Claude Code. Then it waits and turns green with your real numbers the
  moment Claude Code sends its first ones. If Claude Code refuses (offline, an organization's policy), it shows what
  Claude Code printed and the steps to do it by hand, with legacy mode one click away. Closing the window is fine: it
  doesn't come back.
- **Updating from an earlier version** shows one screen, once, about connecting the plugin, unless the plugin already
  sends numbers.
- **Settings → Health:** ten checks with one button each for what to do: Claude Code found and its version, the plugin
  connected and when it last wrote, where your limits come from, legacy mode, today's sessions, notifications (with
  **Send a test alert**), start at login, Codex and updates. **Copy report** copies versions, sources and statuses for a
  bug report, with your home folder written as `~`.
- **Connect…** in the panel when it's waiting for Claude Code, and under the session-lights hint.
- The self-test checks Health and draws the first-start window too (still without showing any window).

### Changed

- When the widget can't read your usage, the panel's button now opens **Settings → Health** instead of the log folder.

---

## 2026-11-20 — Codex beside Claude

### Added

- **Codex's limits in the panel.** If you also use OpenAI Codex, its 5-hour and weekly limits (and any other limit
  Codex reports) get a card of their own under Claude's: the plan, a bar per window, the reset, and the same pace
  line Claude's windows have. The numbers come from the session log Codex writes on your computer after each answer
  (`~/.codex/sessions`, or `CODEX_HOME`): no sign-in, no network, nothing else of Codex's is read. The card says
  where they come from, shows them "as of" once Codex has been quiet for ten minutes, and says so when Codex runs in a
  mode that reports no limits (an API key, some `codex exec` runs).
- **"Codex 91%" on the pill** when Codex is past your orange threshold and closer to a limit than Claude, in the
  usual colors; notifications for Codex's windows name it ("Codex: 5-hour window at 86%").
- **Settings → Codex** turns it off. Without Codex on your computer there is no trace of it, and Settings says it
  wasn't found; once Codex runs, the card appears by itself.
- **A guide for adding other tools** (`docs/PROVIDERS.md`), with Codex as the worked example.
- The self-test checks the Codex reader too.

### Changed

- Screen readers read the usage card's rows and the new tool cards as "5-hour window: 43% · Resets in 2h 5m"
  instead of their internal field list.

---

## 2026-11-19 — a seat-belt for subagents

### Added

- **A budget guard in the Claude Code plugin** (plugin 2.3.0). When you are close to a limit — 90% of the 5-hour
  window, the pace saying you will hit it within 15 minutes, or 97% of the week — and Claude is about to start
  subagents, Claude Code asks you once: **Run anyway**, **Use Haiku for subagents** or **Skip subagents** (Claude
  then carries on without them). Your answer holds for ten minutes, so a fan-out of six subagents asks once, not six
  times. Prefer no question? Set `guard` to `downshift` in `/config` and subagents run on the smaller model with a
  short notice, or to `off`. The threshold, the lead time and the model are options too. It never asks in `claude -p`
  or the Agent SDK, never refuses a subagent unless you chose to skip, and never changes the main conversation.

## 2026-11-19 — your year with Claude Code

### Added

- **A year card.** From 1 December to 31 January the panel's **Share** menu has one more card: **My 2026 with Claude
  Code** (in January, the year that just ended). It shows the year's tokens, what they would have cost at API list
  prices (an estimate, and labelled one), your sessions and active days, your favorite model and your busiest day,
  wide or portrait, dark or light, with Copy, Save and Post like the other cards. It never names a project.
- **Read once, then kept.** The first time, the widget reads the year's transcripts in the background and counts
  them under the panel's header while it does (about two seconds for 400 MB of transcripts here). Finished days are
  kept as small daily summaries in its data folder, so later it reads only what was written since. The numbers are
  counted exactly as the session cards count them, and are counted again if your time zone or the price list
  changes.

## 2026-11-19 — quiet while you're not working

### Changed

- **Nothing to do while Claude Code is idle.** The widget used to look at every transcript in `~/.claude/projects` —
  thousands of files for a heavy user, subagents included — several times a minute, forever. It now reads the folder
  once at start and then lets the system tell it what changed (a full look again every ten minutes, as a safety
  net, and once a minute only where the folder can't be watched, such as some network or synced folders). On 5,000
  transcripts a refresh's session work went from about 0.65 s to under a millisecond.
- **A new session shows up at once.** The pill and the open panel follow Claude Code's transcripts as they are
  written (about once a second while a session works) instead of waiting for the next 15-second tick.

### Fixed

- **The longer session views no longer re-read everything.** The parsed-transcript cache emptied itself completely
  once it held 256 sessions, so a busy month's view parsed every transcript again on each refresh; it now keeps the
  512 most recently read.

## 2026-11-18 — share cards

### Added

- **Share your usage as an image.** The panel's **Share** button makes a designed card of today: the 5-hour and
  weekly rings (or this month's spend against your cap), the pace line, today's sessions, their estimated cost and
  your most used model. **Copy image** puts it on the clipboard; **Save image…** saves a PNG to Pictures;
  **Post on X / LinkedIn / Bluesky** copies it and opens that site's share page with a short line and the project's
  link, for you to paste the image into.
- **Share one session.** Each session card has a **Share** link: what that session cost, how long it ran, the model,
  tokens, cache hits and context.
- **Private by default.** A shared card never names your project, folder, session, branch, repository or account;
  Settings → **Share card** → **Project names** adds the project and session name if you want them. The same section
  picks the shape: wide (1200×630) or portrait (1080×1350). Estimates are labelled as estimates.

## 2026-11-18 — releases you can check

### Added

- **Every release is smoke-tested before it is published.** The release starts the very files you download — both
  Windows builds and both Mac apps — and stops if one of them can't load its tray icon, a screen, the price list or
  its settings.
- **Signed build provenance.** Each file on the Releases page can be checked with
  `gh attestation verify <file> --repo mohammad-k-shah/stickyAIusage`: it shows the workflow run and the commit the
  file was built from. The release notes say how, next to the checksums.
- **`--self-test`** for both apps: `StickyAIusage.exe --self-test report.txt` (or the Mac app's binary) checks that
  this copy starts, in a temporary profile and without a window, and writes one PASS or FAIL line per check. Your
  settings and a running copy aren't touched.

### Changed

- Builds made by GitHub Actions record no build-machine paths, and their symbols point at the source on GitHub.
- Weekly dependency updates (NuGet and GitHub Actions) and code scanning (CodeQL) for the repository.

## 2026-11-18 — a Windows installer

### Added

- **A Windows installer.** Each release has `StickyAIusage-Setup-x64.exe`: it installs Sticky AI Usage for you only
  (no admin rights) into `%LOCALAPPDATA%\Programs\Sticky AI Usage`, adds a Start Menu shortcut and an entry in
  Settings → Apps, and offers to start it. It never closes a running copy: exit it from the tray first, then run
  Setup again. Removing it takes away its "Start with Windows" entry and asks whether to delete your settings (a
  silent removal keeps them). It is also what the coming winget package installs.

## 2026-11-18 — which session needs you

### Added

- **Session lights.** With the Claude Code plugin, the pill shows a dot per Claude Code session: amber (pulsing)
  when it waits for you (a permission, a question, an MCP server's input), green while it works, a grey ring when
  it's done, red after a rate limit. Up to four, waiting ones first; one dot in the taskbar or menu bar. Hover them
  for which session waits for what, and for how long.
- **"Claude needs you".** One notification when a session has waited 20 seconds, naming the project and what it
  waits for. Once per wait. Optional (off unless you turn it on): "Claude finished" when a task of a minute or more
  is done. Both in Settings → Usage alerts; quiet hours and the mute apply.
- **Status on each card.** "Needs you: permission", "Working", "Done 3m ago" or "Rate limited · resets 4:30 PM"
  instead of "Live"; the cards that need you come first.
- **Settings → Session lights** to turn the dots off.
- **Plugin 2.2.0** records what each session is doing. Only the kind of wait and, for a permission, the tool's name:
  never your prompt, a command, a path or a message.

### Changed

- **Lighter session cards.** A card shows its title, status, cost, model and context bar; the chip rows, git and
  the footer open with **Details** (and stay open while the app runs).
- Without the plugin the panel says how to get the lights: "Connect to Claude Code to see which session needs you".

---

## 2026-11-18 — a heads-up before you're blocked

### Added

- **Notifications.** One quiet notification (a Windows toast, a macOS banner) when a window crosses the orange or
  red threshold, when at this pace you'd hit a limit within 30 minutes ("5-hour limit at about 4:12 PM"), when you
  hit it, and "You're back" when a busy window resets. On a weekly window or a monthly cap it also warns, at most
  once a day, when the pace would run it out before it starts over. Never the same one twice, across restarts too.
  Clicking a Windows notification opens the panel, and the pill pulses twice when it sends one (not when your
  computer asks for less motion).
- **Settings → Usage alerts** gained the switches (all off with one, or each kind on its own), quiet hours (for
  example 22:00 to 07:00; alerts in them are skipped, not saved for later) and **Send a test alert**.
- **Mute alerts for an hour** in the tray and menu bar menus.

### Changed

- The orange and red thresholds in Settings → Usage alerts now also decide when a notification comes, not only the
  ring's color. macOS shows the banners through Apple's own script runner, so macOS lists them under Script Editor
  (allow it once in System Settings → Notifications).

## 2026-11-18 — one line to install

### Added

- **Install with one line.** Windows: `irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1 | iex`
  in PowerShell. macOS: `curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh`
  in Terminal. Each downloads the newest release, checks it against the release's checksums and installs nothing if
  it doesn't match; no admin rights, no "Windows protected your PC" prompt, a Start Menu shortcut on Windows.
  Running it again updates the app; `-Uninstall` / `--uninstall` removes it (your settings stay unless you add
  `-Purge` / `--purge`). Neither ever closes a running copy: they ask you to exit it first.
- **Checksums for every download.** Each release lists the SHA-256 of every file in `checksums.txt`.
- **A small Windows download.** If you already have the .NET 10 Desktop Runtime, `StickyAIusage-win-x64-small.zip`
  is the same app at well under 1 MB instead of about 66 MB.

### Changed

- The Windows zip holds only `StickyAIusage.exe` (the two debug-symbol files are now inside it, so error reports
  keep their line numbers). Betas and release candidates are marked as pre-releases on the Releases page.

## 2026-11-18 — stays where you put it

### Fixed

- **Windows remembers the theme.** Every start used to switch back to dark, whatever you had chosen. New installs
  (on Windows and macOS) now follow your computer's light or dark appearance; an installed copy keeps the theme it
  has.
- **Windows remembers where the widget was.** Every start used to put it back by the tray. Now it opens where you
  left it; "Reset widget position" docks it by the tray again, and stays docked. If its spot is on a monitor that's
  gone, it waits by the tray and goes back once that monitor is.
- **Times follow your clock format.** A session card's start time was always "3:05 PM"; it now reads "15:05" on a
  24-hour clock, like the reset and limit times.
- **No more dead ends.** When there are no numbers, or they are old, the panel's usage card says why in one line,
  with the one thing to do about it ("Open log folder", or "Open Settings" when plan limits are turned off),
  instead of a tooltip pointing at the log folder. The sign-in tooltip names the command that renews it
  (`claude auth login`), not Claude Desktop, which doesn't.
- **An error no longer closes the widget silently.** An error inside the widget is written to `diagnostics.log` and
  the widget keeps running; one it can't survive is written there before it closes, and a failed start says so.
- **Files are never left half written.** The usage cache, the rate-limit file, today's spend and the Mac's login item
  are replaced in one step, like the settings. Saving while another program had the file open could fail on
  Windows; it now waits a moment and tries again.
- **Git and GitHub in your project folders.** git runs with a repository's own monitor hook, external diff and text
  filters switched off and never waits on a password prompt; `gh` is asked about pull requests only for
  repositories on GitHub, and at most every five minutes each (it was every 30 seconds, for every repository).

### Added

- **Which build is this?** The last line of Settings names the version, the profile and the usage source, and so
  does the first line `diagnostics.log` writes at each start.

## 2026-11-17 — your limits inside Claude Code

### Added

- **`/pace` in Claude Code.** One to three lines: how much of each limit is used, when it resets, and when you'd
  hit it at your current pace ("5-hour: 43% used · resets 4:30 PM · at this pace: limit ~4:12 PM"), plus the
  session's context and cost. `/pace json` gives the same as JSON. It answers at once, also while Claude is
  working, and works on any OS with or without the widget; with the widget running, both show the same limit time.
- **Claude can look up your limits.** Ask "how much of my limit is left?" and Claude answers with live numbers from
  the plugin's `usage_status` tool; it is told to check before long or parallel work.
- **A quiet band above the prompt** once a limit is half used or the pace says you'll hit it before the reset:
  `● 5h 89% · limit ~3:27 PM · week 36%`, orange within the hour, red within 30 minutes. In the VS Code extension
  the status line says the same.
- **A note for Claude near a limit:** from 80% of a window, one short note beside your prompt asks Claude for smaller
  steps and no needless parallel subagents; at most once every ten prompts, and your text is never changed.
- Every part can be turned off in `/config` (`band`, `contextNote`, `contextNoteThreshold`, `exposeTool`, and
  `feed` for the widget's files). The plugin still makes no network request and no model call. Plugin version 2.1.0.

### Fixed

- After you dismissed the plugin tip, its text stayed in the hidden notice bar (both apps), so a screen reader or a
  later repaint could still find it.

## 2026-11-17 — will you make it to the reset?

### Added

- **A pace forecast.** At your current pace, the pill tells you when you'd hit the 5-hour limit if that comes
  before the reset ("5h: 88% - limit ~1:34 PM"; in the taskbar or menu bar "→ 1:34"), yellow, then orange within
  the hour and red within 30 minutes. "limit reached" says when it resets. The panel adds a line under each window:
  when the limit comes, or where the window ends up, and for the week how you compare with an even pace. If only
  your weekly limit is two days or less away, the pill says that instead. On a monthly cap the panel projects the
  month ("$212 by Nov 30 (cap $200)") and the pill warns when that passes the cap. It needs about ten minutes of
  activity, never shows a time in the past or after the reset, and the tooltip says what it is based on.
  **Settings → Pace forecast** turns it off.
- **The ring eases to a new value** and the forecast fades in; neither moves when your computer asks for less
  motion.
- **For the Claude Code plugin:** the widget writes its forecast to `feed/forecast.json`, so the plugin can show the
  same numbers.

### Changed

- **Times of day follow your computer's clock format**: reset times read "Resets Fri 15:30" where clocks are
  24-hour.

---

## 2026-11-17 — session costs at today's prices

### Fixed

- **Session costs for the four newest models were wrong, and now follow Anthropic's current price list.** Claude
  Fable 5.2 and Claude Opus 5.5 sessions showed about 25% too much, Claude Sonnet 5.5 counted cache reads at twice
  their price, and Claude Haiku 5.5 showed about ten times its cost. Haiku 5.5's higher price for prompts over
  100,000 tokens and Fable 5.2's fast mode are now counted.
- **Claude Haiku 5.5 sessions show their 1M-token context window** (it said 200K).
- **Model ids from Google Cloud** (`claude-opus-4-5@20251101`) are priced as the right model.

### Added

- **A model the price list doesn't know yet shows its cost with ≈**, and the card says why, instead of quietly
  using another model's prices.
- **Claude Code's own session cost.** With the plugin, a session card shows the cost Claude Code itself reports
  (labelled "cost" instead of "est. cost"), as long as the plugin saw the session from its start.
- **Settings → Check for updates** (off unless you turn it on): once a day it fetches the latest price list from
  this project on GitHub, so a price change or a new model doesn't wait for the next release. Nothing about you is
  sent.
- **The price list is a data file** (`data/models.json`): adding a model is a JSON-only pull request — see
  CONTRIBUTING.

---

## 2026-11-17 — choose where your limits come from

### Added

- **Settings → Usage source**, on Windows and macOS: **Plugin** (official: your limits from Claude Code through
  the companion plugin, no network, your sign-in never read), **Automatic** (the plugin, and Claude's usage
  service whenever the plugin has nothing current), **Legacy** (the usage service only, as before the plugin —
  for monthly spend caps in dollars and credits) or **Off**. Automatic and Legacy are marked unofficial, with what
  they do to your sign-in spelled out. The change applies when you press Save; no restart.
- **The flyout says what to do next, and copies the command for you:** sign in again (`claude auth login`),
  install the plugin, or — once, while your numbers come from the usage service — a tip that the plugin can give
  them without your sign-in (Dismiss hides it for good).
- **The Mac now helps you sign in again** like Windows does: the message and a **Copy command** button.

### Changed

- **New installs start on the plugin and never touch your sign-in.** If you're updating from an earlier version
  nothing changes for you: you stay on Automatic and keep the numbers you had. Choose Plugin in Settings to stop
  the widget reading your sign-in.
- **"Waiting for Claude Code"** instead of "Usage unavailable" while the plugin hasn't sent anything yet.

---

## 2026-11-17 — your limits straight from Claude Code

### Added

- **A Claude Code plugin that hands your limits to the widget** (beta). Install it once with
  `/plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage`; from then on Claude Code itself
  passes your 5-hour and weekly limits (or a gateway's spend limit) to the widget after every turn — in the
  terminal, in Claude Desktop's Code tab and in VS Code. No sign-in is read and no request is made for these
  numbers, and the widget refreshes the moment a turn ends instead of polling.
- **`ClaudeSource` in `settings.json`** chooses where the limits come from: `auto` (the plugin, and Claude's
  usage service whenever the plugin has nothing current — the default), `feed` (the plugin only), `legacy` (the
  usage service only, as before) or `off`.
- **A spend limit known only as a percentage** shows as "63% of limit", and windows Claude Code reports under a
  new name get their own line in the flyout instead of being dropped.

### Changed

- **Numbers from the plugin say how old they are honestly:** "As of: 12m ago — updates while Claude Code is
  active" once the last turn is more than three minutes old, instead of "Updated", because nothing is failing or
  retrying in between.
- **"Usage unavailable" now says why** when it knows (for example, waiting for Claude Code's first response).

---

## 2026-11-17 — profiles and a sturdier settings file

### Added

- **Opening the app again opens the widget that's already running** — it shows the pill (if hidden) and opens
  the flyout. It used to do nothing at all, which looked as if the app hadn't started.
- **`--profile <name>` runs a second, separate copy**, for example a build you're testing next to the one you
  use every day. Each profile has its own settings, cache and log (`StickyAIusage-<name>` beside the usual
  folder), its own tray icon tooltip and its own Start with Windows / Launch at login entry, so it never
  changes the everyday copy. `STICKYAIUSAGE_PROFILE` does the same, and `STICKYAIUSAGE_DATA_DIR` puts the data
  folder anywhere you like.
- **`CLAUDE_CONFIG_DIR` is respected.** If you moved Claude Code's folder with it, the widget now finds your
  sessions (and, on Windows, your sign-in) there instead of in `~/.claude`.

### Changed

- **Settings are saved safely.** The file is written in one step, so a crash or a full disk can't leave it
  half-written, and the previous version is kept as `settings.json.bak`; a damaged file is replaced by that
  backup at the next start instead of resetting everything. Settings written by a newer version keep the
  options this one doesn't know.

### Fixed

- **After signing in again, the widget now picks up the new sign-in at once.** It used to wait up to ten more
  minutes after showing "Sign in to Claude" when the new sign-in came soon after the old one expired.

---

## 2026-11-14 — .NET 10

### Changed

- **The apps now run on .NET 10.** .NET 8, the runtime inside every earlier download, stopped getting security
  fixes on 10 November 2026; .NET 10 gets them until November 2028. The widget looks and works the same.
- **macOS 15 (Sequoia) or later is now required**; it was macOS 12. That is the oldest macOS .NET 10 supports.
  Windows 11 is unchanged.
- **Building from source needs the .NET 10 SDK**, and the build now stops at any warning instead of letting
  it through.

---

## 2026-09-22 — day target

### Added

- **Day target** for monthly spend cap accounts (Enterprise): the pill shows `Today: $4.20 42%` with its own
  ring, between the usage and the session cost, and the panel's usage card adds a "Today" row with a bar.
  - **Settings → Widget → Day target** sets the daily target in dollars (default $10) and which parts the pill
    shows: the amount, the % of the target, the ring — any one, two or all three. The switch hides it entirely.
  - The ring uses the same yellow / orange / red thresholds as the usage ring. Over the target, the % keeps
    counting (`136%`) and the ring stays full and red.
  - Today's spend comes from the month's total, so it counts Chat, Cowork and Chrome as well as Code. The
    widget remembers the total when the day began (from yesterday's last reading when it was running). On a
    first launch, only today's Claude Code sessions are known from before it started, so the figure is approximate.
  - On plans with rolling 5-hour and weekly limits (Pro, Max) the section is turned off, with a note saying why.

### Changed

- Both Settings windows scroll when they're taller than the screen, and Save and Cancel stay pinned at the bottom.

---

## 2026-09-20 — the customizable card

### Added

- **A light theme that follows the system.** Pill, panel, settings and customizer all switch with your Mac or
  PC appearance, including the automatic change at sunset. A sun/moon button in the panel header pins either
  one, and **Settings → Appearance** offers `Auto · Light · Dark`. The light palette is designed rather than
  inverted: deeper status colors, softer shadows, and session chips that keep the same colors in both themes.
- **Session cards you arrange yourself** (`Settings → Session cards → Customize`). 49 fields in seven
  groups — Core, Git, Environment, Tokens, Cache, Context, Session — with search, a live preview of the real card, and switches for the context
  bar and the git line. The window opens maximized and stays resizable.
  - Picking a field puts it beside its own group: the row that group already lives on, a new row when there
    isn't one, and only then anywhere with room.
  - Dragging is unrestricted — reorder within a row, move between rows, or drop on the strip at the bottom to
    start a new one.
  - A chip's color is its position in its row (rose, sand, steel, sage, sky, violet, blush, lime), so a drop
    recolors it and everything it passed. Eight colors for eight slots: a row never repeats one.
  - A row holds eight chips and a card holds eight rows. Rows with room light up while you drag; a chip
    dropped on a full row stays where it was, and the heading says why for five seconds.
- **The layout is stored readably** in `settings.json` as `"model,effort|in,out,cached,total|…"`, one row per
  `|`. Unknown field ids are ignored instead of breaking the file.

### Changed

- **The pill reads more cleanly** on plan accounts: `5h: 43% - Resets: 2h 6m │ 1w: 14% │ Updated: just now`,
  and `5h: 43% - 2h 5m │ 1w: 14%` while it's docked in the bar.
- **The Windows pill now has a compact form too**, switching by position — compact while its middle is inside
  the taskbar, full when you drag it onto the desktop. macOS squeezes in steps to fit the menu bar beside the
  notch: the ring goes first, then the text and spacing tighten, and only then does it drop below the bar.
- **Clicking outside the panel closes it**, the way a menu does. (On macOS the app activates itself when the
  panel opens; as a background app it would otherwise never be deactivated.)
- **Compactions is no longer on the default card.** It's still in the catalog. Settings holding exactly the old
  default read as the new one, since the app had saved that string itself.
- **"On the card" and "Preview"** wear the same band as the catalog's group headings, at a fixed height so the
  row-full notice can't nudge them.

### Fixed

- **A chip dropped on a full row used to disappear.** The drop went through, the row was truncated back to
  eight on the way out, and the dragged chip was gone. It's now refused, and the card is left alone.
- **Sessions where nothing happened are no longer listed.** Opening Claude Code in a folder writes a transcript
  before you type anything; a session now needs tokens, a cost above $0.00 and activity today.
- **Per-model prices corrected**, and the Mac app renamed to "Sticky AI Usage".

---

## 2026-09-19 — macOS, and plans that aren't a monthly cap

### Added

- **A macOS app** (Avalonia): the same widget as a floating pill that starts inside the menu bar, left of your
  status icons, with a menu bar icon for Settings, Launch at login, Reset widget position, Open log folder and
  Quit. Credentials come from the `Claude Code-credentials` Keychain item, written back through Apple's
  `security` tool with the secret on stdin.
- **Pro / Max support.** The widget detects how the account is billed on every reading: a `spend` block means a
  monthly cap, `five_hour` / `seven_day` windows (or `limits[]`) mean plan limits. Plan accounts get both
  windows, their reset times, and extra-usage credits when the account reports them.
- **A shared core** (`StickyAIusage.Core`, net8.0) behind both apps, so the rules live in one place.
- **Smarter panel placement**: above or below the pill, whichever has room, shifted sideways near a screen edge.
- **MIT license**, and an open-source README with screenshots.

---

## 2026-09-18 — the redesign, and numbers you can trust

### Added

- **A redesigned widget, session panel and settings**, and screenshot tests that render every screen to PNG in
  CI (`ui-screenshots`, later `mac-ui-screenshots`) so UI changes can be reviewed without running the app.
- **A cross-platform test suite** for the core logic.

### Fixed

- **Session cost inflation on forked conversations.** A fork's file also holds the parent's turns; only lines
  whose `sessionId` matches the file's name count now.
- **Segment colors not cycling**, and screen text colors left implicit.

---

## 2026-09-17 — making the meter honest

### Added

- **Self-refreshing sign-in.** The access token is refreshed before it expires, credentials are re-read
  immediately before writing so a token Claude Code refreshed isn't overwritten, and the write is atomic.
- **Adaptive polling**: fast while you're working, slow when you're idle.
- **Stale readings show their age** instead of pretending to be live.
- **The project icon** (a usage gauge with a dollar sign).

### Fixed

- **~4x session-cost inflation**: usage is counted once per `message.id`, not once per content block.
- **Six hours of continuous 429s** against the usage API, from a throttle that never engaged. The cooldown now
  honors `Retry-After` and survives a restart.
- **Expired-token (401) told apart from rate limits and network failures**, so "Sign in to Claude" only appears
  when it's true.
- **The collapsed pill showing a different session than the expanded list.**

---

## 2026-09-16 — first light

- **v1 scaffold**: an always-on-top strip docked beside the Windows tray, a tray icon, the Claude usage
  endpoint, and today's Claude Code sessions read from local transcripts.
- **A draggable widget** that remembers where you put it, and a pause toggle.
