# sticky-usage — the Claude Code plugin of Sticky AI Usage

Your Claude plan limits inside Claude Code: `/pace`, a usage tool Claude can call, a quiet band above the prompt, a
budget guard before subagents start, and the feed for the [Sticky AI Usage](https://github.com/mohammad-k-shah/stickyAIusage) desktop widget. It gets
its numbers from Claude Code itself: no sign-in is read, no server is asked. Works on its own; with the widget
running, both show the same limit time.

## Install

In Claude Code (2.1.287 or later; tested with 2.1.318 and 2.1.325):

```text
/plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage
```

From a shell:

```bash
claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage
```

In Claude Desktop's Code tab: click **+** next to the prompt box, then **Plugins**, **Add marketplace** with
`mohammad-k-shah/stickyAIusage`, then **Add plugin**.

Claude Code may add that some options are "not yet set": every option has a default, so there is nothing to do
(the [Options](#options) say what each one changes).

## What you get

**`/pace`** — one to three lines, also while Claude is working:

```text
5-hour: 43% used · resets 4:30 PM · at this pace: limit ~4:12 PM
Week:   14% used · resets Fri 3:30 PM · under an even pace
Session: context 24% · cost $3.05
```

`/pace json` prints the same as JSON. Claude Code reports a session's limits after its first answer; before that,
`/pace` shows the newest numbers another session saw, marked "(as of 3:27 PM)". Without plan limits at all (an API
key, Bedrock, a plan without windows) it says "No plan limits reported for this session." In `claude -p`, Claude
Code puts "sticky-usage: " before a plugin command's output, so a script reading `/pace json` drops that prefix
first (or reads the widget's files, below).

**A tool for Claude**, `usage_status`: ask "how much of my limit is left?" and Claude looks it up; it also checks
before long or parallel work. Its description costs about 60 tokens of context; `exposeTool` off removes it.

**A band above the prompt**, one line, only when it matters: `● 5h 89% · limit ~3:27 PM · week 36%`. With
`band` on `auto` (the default) it shows once a limit is half used or the pace says you will hit it before the
reset; orange within an hour of the limit, red within 30 minutes. In the VS Code extension, which has no band, the
status line says the same.

**A note for Claude near a limit**: from 80% of a window (`contextNoteThreshold`), one short note rides beside
your prompt ("82% of the 5-hour window is used; it resets at 4:30 PM. Prefer smaller steps and avoid parallel
subagents unless they are needed."), then not again for ten prompts unless the usage moved five points. Your text
is never changed; Claude reads the note, you don't see it.

**A budget guard for subagents.** Near a limit, before Claude starts subagents, one question in Claude Code's own
dialog:

```text
About 8% of the 5-hour window is left (resets 4:30 PM). Starting subagents now will probably use it up.
Start the subagents?
  1. Run anyway
  2. Use Haiku for subagents
  3. Skip subagents
```

"Near a limit" is the 5-hour window at `guardThreshold` (90%) or more, the pace saying the 5-hour limit comes within
`guardLeadMinutes` (15), or the week at 97%. **Run anyway** starts them as Claude asked; **Use Haiku for subagents**
starts them on `guardModel` (`haiku`); **Skip subagents** refuses them, and Claude reads "The user chose to save usage
budget; continue without parallel subagents." and carries on alone. The answer holds for ten minutes, so a fan-out of
six asks once; closing the dialog runs them. `guard: downshift` uses `guardModel` without asking and says so in a
toast once every ten minutes; `guard: off` adds nothing. Resumed subagents are never asked about, and a fork (which
always runs on the session's model) is offered only Run anyway or Skip. Where no one can answer (`claude -p`, the
Agent SDK) the guard does nothing. It never changes the main conversation's model.

**Session lights** for the widget: the feed file also says whether the session is working, waiting for you or
done, and for a wait what kind of answer (`permission` with the tool's name, `question`, or `input` for an MCP
server). The widget shows a dot per session and notifies when one has waited 20 seconds. The plugin learns it from
Claude Code's own events (turns, permission requests, notifications, tool results, the AskUserQuestion dialog) and
never records your prompt, a command, a path or a message from them.

**The pace** ("limit ~4:12 PM") is an estimate from the last 30 minutes of use (24 hours for the weekly window).
With the desktop widget running, the plugin takes the widget's own forecast so both agree; without it, the plugin
works it out from the readings it keeps, after about ten minutes of activity. Both follow one definition, pinned
by shared test cases.

## What it reads, writes and adds

| | What |
|---|---|
| Reads | `$.session.usage()` (plan-limit windows, context fill, session cost) and the session's id, folder, model, version and surface; the widget's `forecast.json` in the feed folder below, when it is there; the names of Claude Code's turn, permission, notification, tool and elicitation events (and from them only a tool's name and a notification's type) |
| Writes | the feed: one small JSON file per session (`claude-<session id>.json`) and `plugin.json` in the feed folder below; its own store (`$.store`, a JSON file Claude Code keeps for the plugin under its configuration folder, `plugins/store/`): the last 200 plan-limit readings per window, shared by your sessions, for the pace |
| Adds | the `/pace` command, the `usage_status` tool, the band, the note beside your prompt, and near a limit one question (or, in `downshift`, a smaller model) before subagents start |

Feed folder:

| | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\StickyAIusage\feed\` |
| macOS | `~/Library/Application Support/StickyAIusage/feed/` |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/StickyAIusage/feed/` |
| Any OS | `STICKYAIUSAGE_FEED_DIR`, when it is set to a full path |

The feed is written when a session starts, when a turn starts and ends, when Claude Code measures new usage, when
the session ends, when the session starts or stops waiting for you, and on a one-minute heartbeat if something
changed (at least every two minutes, so the widget can tell a closed session from a quiet one). The widget reads these files; nothing else
does.

## What it can reach

This is what `claude plugin validate --strict ./plugin` reports for this version:

```text
hooks: session.start, command.run{command=pace}, tool.call{tool=mcp__sticky-usage__usage_status}, session.measure,
       prompt.submit, ui.render{component=AbovePrompt}, agent.spawn, turn.start, turn.complete, classic.PermissionRequest,
       classic.PostToolUse, classic.PostToolUseFailure, classic.PermissionDenied, classic.Notification,
       classic.Elicitation, classic.ElicitationResult, tool.call{tool=AskUserQuestion}, classic.StopFailure,
       session.end
calls: $.clock.after (via track), $.clock.every, $.clock.now, $.command.register, $.env.get (via resolveFolder), $.fs.exists (via
       resolveFolder), $.fs.read (via readWidgetForecast), $.fs.write, $.session.cwd (via writeRecord), $.session.id
       (via writeRecord), $.session.model (via writeRecord), $.session.surfaces (via guardSpawn, showStatusLine, writeRecord),
       $.session.usage (via readUsage), $.session.version, $.store.get (via currentStatus, recordSamples),
       $.store.set (via recordSamples), $.tool.register, $.ui.ask (via askOnce), $.ui.invalidate, $.ui.log (via
       logFailure, logSurfaceFailure), $.ui.resolve, $.ui.status (via showStatusLine), $.ui.toast (via guardSpawn)
env reads: HOME, LOCALAPPDATA, STICKYAIUSAGE_FEED_DIR, USERPROFILE, XDG_DATA_HOME
env writes: nothing
```

No `$.http`, `$.process` or `$.model`: it never talks to the network, never runs a command and never spends
your usage. (`$.tool.register` declares the `usage_status` tool; Claude Code itself serves it to Claude.) A failure
is logged once and never holds up a turn or a prompt. The permission, notification, tool, question and elicitation
hooks only watch: each passes the event on unchanged and has a `.catch` that does the same if it fails. The
budget guard's `agent.spawn` hook changes a subagent's start only as described above (near a limit, with someone to
ask); if it fails, the subagent starts as Claude asked.

## Options

In `/config`:

| Option | Default | What it does |
|---|---|---|
| `feed` | on | Write the feed for the widget. Off: the plugin writes no feed files (`/pace` and the rest still work). |
| `band` | `auto` | `auto`, `always` (whenever there are plan limits) or `never`. |
| `contextNote` | on | The note for Claude near a limit. |
| `contextNoteThreshold` | 80 | The percent of a window at which the note starts (1–100). |
| `exposeTool` | on | The `usage_status` tool for Claude. |
| `guard` | `ask` | The budget guard: `ask`, `downshift` (the smaller model without asking) or `off`. |
| `guardThreshold` | 90 | The percent of the 5-hour window at which the guard starts (1–100). |
| `guardLeadMinutes` | 15 | The guard also starts when the pace says the 5-hour limit is this close (0–600 minutes). |
| `guardModel` | `haiku` | The model subagents run on when you choose it: an alias (`haiku`, `sonnet`) or a full model id. |

## Remove it

`/plugin` → uninstall `sticky-usage`. The widget then says "As of …" and stops updating from it; delete the
`feed` folder above to remove the last files, and `plugins/store/sticky-usage*.json` under Claude Code's
configuration folder (`~/.claude` unless `CLAUDE_CONFIG_DIR` says otherwise) for the kept readings. That is
everything it leaves behind.

## License

MIT, like the rest of the repository.
