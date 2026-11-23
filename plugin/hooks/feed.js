// Pure helpers of the Sticky AI Usage plugin: where the feed folder is, what
// a record holds and when it is worth writing again. Nothing here touches the
// engine (`$`), so `node --test` checks all of it (feed.test.mjs).

/** Keep in step with .claude-plugin/plugin.json (a test checks it). */
export const PLUGIN_VERSION = '2.3.1'

/** The record format; the widget refuses a schema it does not know. */
export const RECORD_SCHEMA = 1

/** How often the heartbeat looks at the session. */
export const HEARTBEAT_MS = 60_000

/** A record that has not changed is still rewritten after this long, so a
 *  running session's file is never older than about three minutes. */
export const QUIET_REWRITE_MS = 120_000

const isAbsolute = (path) => /^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(path)

const trimEnd = (path) => path.replace(/[\\/]+$/, '')

/**
 * The folder the widget reads, from the environment Claude Code runs in:
 * STICKYAIUSAGE_FEED_DIR when it is a full path (tests, a second copy);
 * Windows: %LOCALAPPDATA%\StickyAIusage\feed; macOS:
 * ~/Library/Application Support/StickyAIusage/feed; elsewhere:
 * ${XDG_DATA_HOME:-~/.local/share}/StickyAIusage/feed. Null when none can be
 * worked out (then nothing is written).
 */
export function feedFolder({ override, localAppData, home, xdgDataHome, isMac }) {
  if (override && isAbsolute(override)) return trimEnd(override)
  if (localAppData && isAbsolute(localAppData)) return `${trimEnd(localAppData)}\\StickyAIusage\\feed`
  if (!home || !isAbsolute(home)) return null
  if (isMac) return `${trimEnd(home)}/Library/Application Support/StickyAIusage/feed`
  const data = xdgDataHome && isAbsolute(xdgDataHome) ? trimEnd(xdgDataHome) : `${trimEnd(home)}/.local/share`
  return `${data}/StickyAIusage/feed`
}

/** A path in the folder, with the folder's own separator. */
export function inFolder(folder, name) {
  return folder + (folder.includes('\\') ? '\\' : '/') + name
}

/** `claude-<sessionId>.json`, the id reduced to characters safe in a file name. */
export function sessionFileName(sessionId) {
  const safe = String(sessionId ?? '').replace(/[^A-Za-z0-9_-]/g, '_').slice(0, 100)
  return `claude-${safe || 'unknown'}.json`
}

const finite = (value) => (typeof value === 'number' && Number.isFinite(value) ? value : undefined)

/** The context figures without the breakdown (which `usage()` never returns
 *  unasked, but a future build might). */
function contextOf(context) {
  if (!context || typeof context !== 'object') return null
  const out = {}
  for (const key of ['tokens', 'window', 'percent']) {
    const value = finite(context[key])
    if (value !== undefined) out[key] = value
  }
  return out
}

/**
 * One session's record, schema 1. `usage` is what `$.session.usage()`
 * answered (null when the call is missing or failed): its `rateLimits` and
 * `cost` are passed on exactly as Claude Code gave them.
 */
export function buildRecord({
  usage, sessionId, cwd, model, surface, claudeCodeVersion, now, rateLimitsAt, state, stateSince, attention, lastTurnMs,
}) {
  return {
    schema: RECORD_SCHEMA,
    source: 'claude-code-mod',
    pluginVersion: PLUGIN_VERSION,
    claudeCodeVersion: claudeCodeVersion ?? null,
    surface: surface ?? null,
    sessionId: String(sessionId ?? ''),
    cwd: cwd ?? null,
    model: model ?? null,
    observedAt: now,
    startedAt: finite(usage?.startedAt) ?? null,
    rateLimitsAt: rateLimitsAt ?? null,
    context: contextOf(usage?.context),
    rateLimits: Array.isArray(usage?.rateLimits) ? usage.rateLimits : null,
    cost: usage?.cost ?? null,
    state,
    stateSince: stateSince ?? now,
    attention: attentionOf(attention),
    lastTurnMs: finite(lastTurnMs) ?? null,
  }
}

/** What a waiting session waits for: the kind and, for a permission, the
 *  tool's name. Anything else an attention object might hold stays out. */
function attentionOf(attention) {
  if (!attention || typeof attention !== 'object' || typeof attention.type !== 'string') return null
  const out = { type: attention.type.slice(0, 32) }
  if (typeof attention.tool === 'string' && attention.tool.length > 0) out.tool = attention.tool.slice(0, 64)
  return out
}

/** The record without its write time: equal keys mean nothing changed. */
export function contentKey(record) {
  const { observedAt, ...rest } = record
  return JSON.stringify(rest)
}

/** Write when forced (a turn or the session ended), when the content
 *  changed, or when the file would otherwise grow older than the widget's
 *  "live" window. */
export function shouldWrite({ force, key, lastKey, now, lastWrittenAt }) {
  if (force || key !== lastKey) return true
  return now - (lastWrittenAt ?? 0) >= QUIET_REWRITE_MS
}

/** Whether the process goes on after `session.end` under a new session id
 *  (a /clear, which raises no `session.start`): its records start over as
 *  "started", not "ended". */
export function continuesAfterEnd(reason) {
  return reason === 'clear'
}

/** `plugin.json` in the feed folder: which plugin and Claude Code wrote last. */
export function markerRecord({ claudeCodeVersion, surface, now }) {
  return {
    schema: RECORD_SCHEMA,
    pluginVersion: PLUGIN_VERSION,
    claudeCodeVersion: claudeCodeVersion ?? null,
    surface: surface ?? null,
    observedAt: now,
  }
}
