# Changelog

What changed and why. Tagged versions are published on the
[Releases page](https://github.com/mohammad-k-shah/stickyAIusage/releases). **v1.0.0** is the first public
release and includes everything below; the dated sections record how it was built up.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely: grouped by what it means
for someone using the widget, not by file.

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
