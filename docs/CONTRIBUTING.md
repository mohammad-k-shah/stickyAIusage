# Contributing

Issues and pull requests are welcome: bug reports, ideas, a new field for the session card, or a plan layout
that doesn't look right on your account (real-world plan shapes are the hardest part to test).

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

**Good places to start:** a new field for the session card (one entry in `SessionFields.All` and it appears in
both apps), a price-table update, or the Linux build nobody has written yet.

## The code

Two thin apps over one shared, tested core, all **.NET 8**:

```text
src/
├── StickyAIusage.Core/      shared: usage endpoint + plan detection, token refresh (file / Keychain),
│                            transcript reader, git, pricing, view models, field catalog + card layout
├── StickyAIusage/           Windows app (WPF): taskbar widget, tray icon, settings, customizer
└── StickyAIusage.Mac/       macOS app (Avalonia): menu bar widget, menu bar icon, settings, customizer
tests/
├── StickyAIusage.Tests/         xunit, any OS: core logic, plan detection, card layout, Keychain format
├── StickyAIusage.UiTests/       Windows: renders every WPF screen to PNG
└── StickyAIusage.Mac.UiTests/   any OS: renders every macOS screen to PNG (Avalonia headless + Skia)
scripts/build-mac-app.sh         self-contained, ad-hoc signed "Sticky AI Usage.app"
```

```bash
dotnet build StickyAIusage.sln -p:EnableWindowsTargeting=true   # the flag is only needed off Windows
dotnet test tests/StickyAIusage.Tests
dotnet test tests/StickyAIusage.Mac.UiTests   # set UI_SCREENSHOT_DIR to keep the PNGs
dotnet test tests/StickyAIusage.UiTests       # Windows only
```

Every rule that matters — plan detection, accounting, the field catalog, the layout and its colors — lives in
`StickyAIusage.Core`, so a change lands on Windows and macOS at once. CI builds on both and uploads the
rendered screens, so UI changes can be reviewed without running either app.

## Where the numbers come from

The app reads two sources and never calls a model.

| What you see | Where it comes from |
|---|---|
| Monthly spend / plan limits, credits | `GET https://api.anthropic.com/api/oauth/usage`, using the Claude Code sign-in token. A `spend` block means a monthly cap; `five_hour` / `seven_day` (or `limits[]`) mean a Pro/Max plan |
| Session cost, tokens, cache, context, model, effort | Claude Code transcripts in `~/.claude/projects/**/*.jsonl`, read locally |
| Git and PR/CI | `git` and `gh` run against each session's working folder, cached for 30 s |

**Session accounting** (each rule found by comparing against Claude Code's own `/usage`):

- **Once per API response.** Claude Code writes one transcript line per content block, each repeating the same
  usage; usage is counted once per `message.id`.
- **Forks count only their own turns.** A forked conversation's file also holds the parent's turns; only lines
  whose `sessionId` matches the file name count.
- **Subagents are separate.** `isSidechain` turns and `subagents/*.jsonl` files aren't part of your session.
- **Nothing-happened sessions are hidden.** A session needs tokens, a cost above $0.00 and activity today.
- **Incremental reads.** Each refresh reads only the bytes appended since the last one.
- **Cost is an estimate.** Tokens × a per-model price table; Anthropic's monthly figure is authoritative.

**Polling and sign-in:** 30 s while a transcript changed in the last 2 minutes, 10 min otherwise, plus one
check as soon as a 5-hour or weekly window resets. A `429` backs off for the server's `Retry-After` and the
cooldown survives a restart. The access token is refreshed shortly before it expires; credentials (a file on
Windows, a Keychain item on macOS) are re-read immediately before writing, so a token Claude Code refreshed in
the meantime is never overwritten.

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
`docs` before merging so they land in the right group.

Windows ships self-contained (no .NET install needed to run it); macOS ships an ad-hoc signed app bundle for
Apple Silicon and Intel. The **Build** workflow's artifacts are for us — they need a GitHub sign-in and they
expire — so the Releases page is what people should be pointed at.

## Files the app writes

`%LOCALAPPDATA%\StickyAIusage\` on Windows; `~/Library/Application Support/StickyAIusage/` on macOS, with logs
in `~/Library/Logs/StickyAIusage/`.

| Path | Contents |
|---|---|
| `settings.json` | Settings, the card layout, and the widget's last position |
| `cache/` | Last usage reading and rate-limit cooldown, so a restart doesn't re-query immediately |
| `diagnostics.log` | Errors and state changes, for troubleshooting. Past 1 MB it moves to `diagnostics.old.log`; your home folder is written as `~` |

More: [changelog](CHANGELOG.md).
