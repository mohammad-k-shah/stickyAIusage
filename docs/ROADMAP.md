# Roadmap

What is being built, what comes next and what is further out. It changes with what people ask for: a 👍 on an
issue, or a new one, moves things. Nothing here is a promise of a date.

Want to help with one of these? Look for its issue (labels `good first issue` and `help wanted`), or open one and
say you'd like to take it. [CONTRIBUTING.md](CONTRIBUTING.md) explains how.

## Now

- **Signed downloads.** A code-signed Windows app and a notarized Mac app, so neither shows an "unknown developer"
  warning.
- **winget and Scoop.** `winget install` and a Scoop bucket alongside the one-line install scripts.
- **The companion plugin in Claude Code's plugin directory**, so it installs without typing the marketplace.

## Next

- **Usage history.** The last 7 and 30 days of each window's peaks and estimated cost by day, model and project
  (project names hideable), kept on your computer.
- **Your month and year, recapped.** A monthly recap next to the year card, made from the same daily totals.
- **A status file for other tools.** A documented, versioned `status.json` so tmux, Starship, Rainmeter,
  SketchyBar or Waybar can show your limits, with tested examples.
- **The app in more languages.** Chinese, Japanese and Korean first, then Spanish, Brazilian Portuguese and more,
  with the text in one place so a translation is one file.
- **Pill styles.** Ring only, a small bar, text only or two lines; three sizes.
- **Accessibility.** Screen-reader names for every part of the pill and panel, keyboard paths everywhere, and
  high-contrast themes.
- **More tools beside Claude and Codex.** GitHub Copilot's premium requests through GitHub's own billing interface,
  Gemini CLI from its local files, Cursor (opt-in, clearly labelled when it needs an undocumented interface). The
  [provider guide](PROVIDERS.md) is how they get added.

## Later

- **One UI on every platform, and a Linux app.** The Mac app's UI framework on Windows too, then a Linux build with
  a tray icon.
- **Usage coach.** Weekly tips worked out on your computer, never by a model: which model or project uses most,
  when an earlier `/compact` would have saved context, how your cache hits trend.
- **Theme gallery.** Themes as small files anyone can add with a pull request.
- **Integrations.** Windows Command Palette, Stream Deck, Raycast, Rainmeter.
- **A global shortcut** that opens the panel over any app.
- **A docs site** with how-tos and recipes.

## Recently shipped

Pace forecast and alerts, session lights ("which session needs you"), the in-agent `/pace`, usage tool and budget
guard, share cards and the year card, Codex beside Claude, the first-start walkthrough and Settings → Health,
docking inside the Windows taskbar, demo mode, one-line installs and a Windows installer, an optional update notice,
and smoke-tested, checksummed releases. The [changelog](CHANGELOG.md) has the details.
