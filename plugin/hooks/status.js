// Pure helpers of the plugin's in-agent surface: the samples it keeps for the
// pace, the widget's forecast file, the status that `/pace`, the
// `usage_status` tool, the band and the context note all speak from, and the
// words each of them uses. Nothing here touches the engine (`$`), so
// `node --test` checks all of it (status.test.mjs).

import { MINUTE, lengthOfKind, lookbackMs, pace, sameReset } from './pace.js'

/** The widget rewrites forecast.json at least once a minute while it runs:
 *  a file older than this means the widget is closed. */
export const BRIDGE_FRESH_MS = 2 * MINUTE

/** Samples kept per window kind in `$.store`, shared by every session. */
export const MAX_SAMPLES = 200

/** The band shows (in `auto`) once a window is this full. */
export const BAND_AT = 50

/** A context note is not repeated within this many prompts… */
export const NOTE_EVERY_PROMPTS = 10

/** …unless the fullest window moved this many points since the last one. */
export const NOTE_MOVE_POINTS = 5

export const NO_LIMITS = 'No plan limits reported for this session.'

const finite = (value) => (typeof value === 'number' && Number.isFinite(value) ? value : null)

/** A reset time as Claude Code or the widget gives it (ISO text, seconds or
 *  milliseconds) in milliseconds, or null. */
export function toMs(value) {
  if (typeof value === 'string') {
    const parsed = Date.parse(value)
    return Number.isFinite(parsed) ? parsed : null
  }
  const number = finite(value)
  if (number === null || number <= 0) return null
  return number > 1e12 ? number : number * 1000
}

/** The `$.store` key of a kind's samples, or null for a name not fit for one. */
export function samplesKey(kind) {
  return typeof kind === 'string' && /^[a-z0-9_]{1,40}$/.test(kind) ? `samples:${kind}` : null
}

/** Stored rows (`[at, percent, resetsAt]`) as samples; anything malformed is dropped. */
export function samplesOf(stored) {
  if (!Array.isArray(stored)) return []
  const out = []
  for (const row of stored) {
    if (!Array.isArray(row)) continue
    const at = finite(row[0])
    const percent = finite(row[1])
    const resetsAt = row[2] === null ? null : finite(row[2])
    if (at === null || percent === null || (row[2] !== null && resetsAt === null)) continue
    out.push({ at, percent, resetsAt })
  }
  return out
}

/**
 * The stored rows with one reading added, or the same array when the reading
 * adds nothing (so the caller can skip the write):
 * - an unchanged reading is kept only once a sixth of the lookback passed
 *   (5 minutes for the 5-hour window, as the widget's journal does);
 * - a reading less than a hundredth of the lookback (18 s for 5-hour, ~14 min
 *   weekly) after the one before the newest takes the newest's place, so a
 *   busy day still fits and the series keeps growing;
 * - a reading older than the newest (clock set back) drops the "future" rows;
 * - the newest MAX_SAMPLES are kept.
 */
export function addSample(stored, kind, sample) {
  const rows = Array.isArray(stored) ? stored.filter((row) => Array.isArray(row)) : []
  const at = finite(sample?.at)
  const percent = finite(sample?.percent)
  if (at === null || percent === null) return stored
  const resetsAt = sample.resetsAt == null ? null : finite(sample.resetsAt)
  const row = [at, percent, resetsAt]
  const lookback = lookbackMs(kind, lengthOfKind(kind))

  let kept = rows.filter((r) => finite(r[0]) !== null && r[0] <= at)
  const last = kept[kept.length - 1]
  const beforeLast = kept[kept.length - 2]
  if (last && sameReset(last[2], resetsAt)) {
    if (last[1] === percent && at - last[0] < lookback / 6 && kept.length === rows.length) return stored
    // The newest reading moves forward until it is a hundredth of the
    // lookback past the one before it; so kept readings stay that far apart
    // and a steady stream of readings still builds a series.
    if (beforeLast && sameReset(beforeLast[2], resetsAt) && at - beforeLast[0] < lookback / 100 &&
        percent >= last[1]) {
      kept = kept.slice(0, -1)
    }
  }
  kept.push(row)
  return kept.slice(-MAX_SAMPLES)
}

/**
 * The widget's forecast file (`feed/forecast.json`, schema 1), when it is
 * current: kind → `{ resetsAt, hitsLimitAt, projectedAtReset, confidence }`.
 * Null when the text is missing, malformed, of another schema, or older than
 * BRIDGE_FRESH_MS (the widget is not running).
 */
export function readBridge(text, now) {
  if (typeof text !== 'string' || text.length === 0) return null
  let file
  try {
    file = JSON.parse(text)
  } catch {
    return null
  }
  if (!file || file.schema !== 1 || !Array.isArray(file.windows)) return null
  const updatedAt = toMs(file.updatedAt)
  if (updatedAt === null || Math.abs(now - updatedAt) > BRIDGE_FRESH_MS) return null
  const windows = {}
  for (const w of file.windows) {
    if (!w || typeof w.kind !== 'string') continue
    windows[w.kind] = {
      resetsAt: toMs(w.resetsAt),
      hitsLimitAt: toMs(w.hitsLimitAt),
      projectedAtReset: finite(w.projectedAtReset),
      confidence: w.confidence === 'high' || w.confidence === 'low' ? w.confidence : 'none',
    }
  }
  return windows
}

export const ORDER = ['five_hour', 'seven_day', 'seven_day_opus', 'seven_day_sonnet', 'spend_limit']

/** Readings older than this aren't offered when a session has none of its own. */
export const LATEST_MAX_AGE_MS = 8 * 24 * 60 * MINUTE

/**
 * The newest stored reading of each kind, as limits marked with `asOf`: what `/pace` and
 * the tool fall back on in a session that has no limits of its own yet (Claude Code
 * reports them only after the session's first answer). The limits are the account's,
 * so another session's reading is the latest known.
 */
export function latestLimits(samplesByKind, now) {
  const limits = []
  for (const [kind, samples] of Object.entries(samplesByKind ?? {})) {
    const newest = (samples ?? []).reduce((a, b) => (a === null || b.at > a.at ? b : a), null)
    if (newest === null || newest.at > now || now - newest.at > LATEST_MAX_AGE_MS) continue
    limits.push({ kind, percentUsed: newest.percent, resetsAt: newest.resetsAt, asOf: newest.at })
  }
  return limits
}

const LABELS = {
  five_hour: ['5-hour', '5h', '5-hour window'],
  seven_day: ['Week', 'week', 'weekly window'],
  seven_day_opus: ['Week (Opus)', 'opus week', 'weekly Opus window'],
  seven_day_sonnet: ['Week (Sonnet)', 'sonnet week', 'weekly Sonnet window'],
  spend_limit: ['Spend limit', 'spend', 'spend limit'],
}

/** `[label, short label, noun]` of a window kind; unknown kinds by their name. */
export function labelsOf(kind) {
  if (LABELS[kind]) return LABELS[kind]
  const words = String(kind).replace(/[_-]+/g, ' ').trim() || 'limit'
  return [words[0].toUpperCase() + words.slice(1), words, `${words} window`]
}

/** Weekly and spend windows say how they compare with an even pace, as the widget does. */
const SAYS_EVEN_PACE = new Set(['seven_day', 'seven_day_opus', 'seven_day_sonnet', 'spend_limit', 'month_spend'])

/**
 * What every surface speaks from.
 * @param {object} input
 * @param {object|null} input.usage   `$.session.usage()`'s answer (null when it failed)
 * @param {number} input.now
 * @param {object|null} input.bridge  readBridge()'s answer
 * @param {Record<string, {at: number, percent: number, resetsAt: number|null}[]>} input.samples  by kind
 * @param {object[]} [input.latest]  latestLimits(): used when the session reports none
 */
export function buildStatus({ usage, now, bridge, samples, latest }) {
  const own = Array.isArray(usage?.rateLimits) ? usage.rateLimits : []
  const limits = own.length > 0 ? own : (latest ?? [])
  const windows = []
  for (const limit of limits) {
    if (!limit || typeof limit.kind !== 'string') continue
    const percent = finite(limit.percentUsed)
    if (percent === null) continue
    const resetsAt = toMs(limit.resetsAt)
    const hasReset = resetsAt !== null && resetsAt <= now
    const [label] = labelsOf(limit.kind)
    const window = {
      kind: limit.kind,
      label,
      percentUsed: hasReset ? 0 : percent,
      resetsAt,
      hasReset,
      limitReached: !hasReset && percent >= 100,
      hitsLimitAt: null,
      projectedAtReset: null,
      aheadOfEvenPace: null,
      confidence: 'none',
      forecastFrom: null,
      asOf: finite(limit.asOf),
    }
    if (!hasReset) {
      const lengthMs = lengthOfKind(limit.kind)
      const forecast = pace({ kind: limit.kind, samples: samples?.[limit.kind] ?? [], now, resetsAt, lengthMs })
      // The even pace is plain arithmetic on the reading itself.
      if (resetsAt !== null && lengthMs !== null) {
        const elapsed = Math.min(lengthMs, Math.max(0, lengthMs - (resetsAt - now)))
        window.aheadOfEvenPace = percent - (100 * elapsed) / lengthMs
      }
      const widget = bridge?.[limit.kind]
      if (widget && sameReset(widget.resetsAt, resetsAt)) {
        // The widget's forecast, so the pill and `/pace` agree.
        window.hitsLimitAt = widget.hitsLimitAt === null ? null : Math.max(now, widget.hitsLimitAt)
        window.projectedAtReset = widget.projectedAtReset
        window.confidence = widget.confidence
        window.forecastFrom = 'widget'
      } else if (forecast.confidence !== 'none') {
        window.hitsLimitAt = forecast.hitsLimitAt
        window.projectedAtReset = forecast.projectedAtReset
        window.confidence = forecast.confidence
        window.forecastFrom = 'plugin'
      }
      if (window.confidence === 'none' || window.limitReached) {
        window.hitsLimitAt = null
        window.projectedAtReset = null
      }
      if (window.confidence === 'none') window.forecastFrom = null
    }
    windows.push(window)
  }
  windows.sort((a, b) => rank(a.kind) - rank(b.kind))

  const context = usage?.context && typeof usage.context === 'object' ? usage.context : null
  const tokens = finite(context?.tokens)
  const size = finite(context?.window)
  let contextPercent = finite(context?.percent)
  if (contextPercent === null && tokens !== null && size) contextPercent = (100 * tokens) / size

  return {
    observedAt: now,
    windows,
    context: contextPercent === null ? null : { percent: contextPercent, tokens, window: size },
    costUsd: finite(usage?.cost?.usd),
  }
}

function rank(kind) {
  const index = ORDER.indexOf(kind)
  return index === -1 ? ORDER.length : index
}

/** Date parts in the local zone (the default) or another, for the formatters. */
export function localParts(ms) {
  const d = new Date(ms)
  return { year: d.getFullYear(), month: d.getMonth(), day: d.getDate(), weekday: d.getDay(),
    hours: d.getHours(), minutes: d.getMinutes() }
}

/** Whether this environment's clock format is 12-hour (Intl's hour cycle). */
export function prefersHour12() {
  try {
    const cycle = new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hourCycle
    return cycle === 'h11' || cycle === 'h12'
  } catch {
    return true
  }
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/**
 * Time words for one moment: `time` "4:12 PM" (or "16:12"), `dayTime` the
 * time alone today, "Fri 3:30 PM" within six days, "Nov 24 3:30 PM" beyond.
 */
export function clock({ now, hour12 = true, parts = localParts }) {
  const today = parts(now)
  const time = (ms) => {
    const p = parts(ms)
    const minutes = String(p.minutes).padStart(2, '0')
    if (!hour12) return `${String(p.hours).padStart(2, '0')}:${minutes}`
    return `${p.hours % 12 === 0 ? 12 : p.hours % 12}:${minutes} ${p.hours < 12 ? 'AM' : 'PM'}`
  }
  const dayTime = (ms) => {
    const p = parts(ms)
    if (p.year === today.year && p.month === today.month && p.day === today.day) return time(ms)
    if (Math.abs(ms - now) < 6 * 24 * 60 * MINUTE) return `${WEEKDAYS[p.weekday]} ${time(ms)}`
    return `${MONTHS[p.month]} ${p.day} ${time(ms)}`
  }
  return { time, dayTime }
}

const whole = (percent) => `${Math.round(percent)}%`

/** "at this pace: limit ~4:12 PM", "on pace to end near 61%", "under an even pace", or null. */
function paceWords(window, words) {
  if (window.limitReached) return 'limit reached'
  if (window.hitsLimitAt !== null) return `at this pace: limit ~${words.dayTime(window.hitsLimitAt)}`
  if (window.projectedAtReset !== null) {
    return window.projectedAtReset >= 100 ? 'on pace to use it all before the reset'
      : `on pace to end near ${whole(window.projectedAtReset)}`
  }
  if (SAYS_EVEN_PACE.has(window.kind) && window.aheadOfEvenPace !== null) {
    const ahead = window.aheadOfEvenPace
    return ahead >= 1.5 ? `${Math.round(ahead)} points ahead of an even pace`
      : ahead <= -1.5 ? 'under an even pace' : 'on an even pace'
  }
  return null
}

/** The session's context fill and cost: "context 24% · cost $3.05", or null. */
function sessionWords(status) {
  const parts = []
  if (status.context) parts.push(`context ${whole(status.context.percent)}`)
  if (status.costUsd !== null) parts.push(`cost $${status.costUsd.toFixed(2)}`)
  return parts.length ? parts.join(' · ') : null
}

/** `/pace`: one line per window and one for the session. */
export function paceLines(status, words) {
  const lines = []
  if (status.windows.length === 0) lines.push(NO_LIMITS)
  const width = Math.max(0, ...status.windows.map((w) => w.label.length)) + 1
  for (const w of status.windows) {
    const asOf = w.asOf === null ? '' : ` (as of ${words.dayTime(w.asOf)})`
    const parts = [`${`${w.label}:`.padEnd(width)} ${whole(w.percentUsed)} used${asOf}`]
    if (w.hasReset) {
      parts.push(`reset ${words.dayTime(w.resetsAt)}, starts again with your next message`)
    } else {
      if (w.resetsAt !== null) parts.push(`resets ${words.dayTime(w.resetsAt)}`)
      const said = paceWords(w, words)
      if (said) parts.push(said)
    }
    lines.push(parts.join(' · '))
  }
  const session = sessionWords(status)
  if (session) lines.push(`Session: ${session}`)
  return lines.join('\n')
}

const round1 = (value) => (value === null ? null : Math.round(value * 10) / 10)

/** An instant as ISO 8601 with the local offset ("2026-11-17T16:30:00+05:30"). */
export function isoLocal(ms, offsetMinutes = -new Date(ms).getTimezoneOffset()) {
  if (ms === null) return null
  const shifted = new Date(ms + offsetMinutes * MINUTE).toISOString().slice(0, 19)
  if (offsetMinutes === 0) return `${shifted}Z`
  const sign = offsetMinutes < 0 ? '-' : '+'
  const abs = Math.abs(offsetMinutes)
  return `${shifted}${sign}${String(Math.floor(abs / 60)).padStart(2, '0')}:${String(abs % 60).padStart(2, '0')}`
}

/** `/pace json` and the `usage_status` tool's result. */
export function paceJson(status, words, iso = isoLocal) {
  return {
    schema: 1,
    observedAt: iso(status.observedAt),
    windows: status.windows.map((w) => ({
      kind: w.kind,
      label: w.label,
      percentUsed: round1(w.percentUsed),
      resetsAt: iso(w.resetsAt),
      limitReached: w.limitReached,
      hitsLimitAt: iso(w.hitsLimitAt),
      projectedAtReset: round1(w.projectedAtReset),
      aheadOfEvenPace: round1(w.aheadOfEvenPace),
      confidence: w.confidence,
      forecastFrom: w.forecastFrom,
      asOf: iso(w.asOf),
    })),
    context: status.context ? { percent: round1(status.context.percent), tokens: status.context.tokens,
      window: status.context.window } : null,
    costUsd: status.costUsd,
    summary: paceLines(status, words),
    notes: status.windows.length === 0 ? NO_LIMITS
      : 'Times are local. hitsLimitAt: when the window reaches 100% at the recent pace (an estimate; null when it ' +
        'will not before the reset or there is too little activity to tell). asOf: set when this session has no ' +
        'reading of its own yet and the numbers are the latest another session saw.',
  }
}

/** Whether the band shows: `always` with any limit, `auto` from 50% or with a forecast limit, `never` not. */
export function bandShows(status, mode) {
  if (mode === 'never' || status.windows.length === 0) return false
  if (mode === 'always') return true
  return status.windows.some((w) => w.percentUsed >= BAND_AT || w.limitReached || w.hitsLimitAt !== null)
}

const RINGS = ['○', '◔', '◑', '◕', '●']

/** The band's one line, "◔ 5h 78% · limit ~4:12 PM · week 31%", and its level. */
export function bandLine(status, words) {
  const fullest = Math.max(0, ...status.windows.map((w) => w.percentUsed))
  const ring = RINGS[Math.min(4, Math.max(0, Math.round(fullest / 25)))]
  const parts = []
  let level = fullest >= 95 ? 'error' : fullest >= 80 ? 'warning' : null
  for (const w of status.windows) {
    const [, short] = labelsOf(w.kind)
    parts.push(`${short} ${whole(w.percentUsed)}`)
    if (w.limitReached) {
      parts.push('limit reached')
      level = 'error'
    } else if (w.hitsLimitAt !== null) {
      parts.push(`limit ~${words.dayTime(w.hitsLimitAt)}`)
      const minutes = (w.hitsLimitAt - status.observedAt) / MINUTE
      if (minutes <= 30) level = 'error'
      else if (minutes <= 60 && level !== 'error') level = 'warning'
    }
  }
  return { text: `${ring} ${parts.join(' · ')}`, level }
}

/**
 * The context note's rule. `memory` is `{ prompts, notedAt, notedPercent }`
 * (start with `{ prompts: 0, notedAt: null, notedPercent: null }`); call once
 * per prompt the person typed. Returns the note (or null) and the memory to
 * keep: a note when a window is at or over `threshold` and no note came in
 * the last NOTE_EVERY_PROMPTS prompts, or the fullest window moved
 * NOTE_MOVE_POINTS since the last one.
 */
export function contextNote({ status, threshold, memory, words }) {
  const prompts = (memory?.prompts ?? 0) + 1
  const kept = { prompts, notedAt: memory?.notedAt ?? null, notedPercent: memory?.notedPercent ?? null }
  const hot = status.windows.filter((w) => !w.hasReset && w.percentUsed >= threshold)
  if (hot.length === 0) return { note: null, memory: kept }
  const top = Math.max(...hot.map((w) => w.percentUsed))
  const due = kept.notedAt === null || prompts - kept.notedAt >= NOTE_EVERY_PROMPTS ||
    Math.abs(top - kept.notedPercent) >= NOTE_MOVE_POINTS
  if (!due) return { note: null, memory: kept }

  const said = hot.map((w) => {
    const [, , noun] = labelsOf(w.kind)
    const reset = w.resetsAt === null ? '' : `; it resets ${resetWords(w.resetsAt, words)}`
    return w.limitReached ? `the ${noun} is used up${reset}` : `${whole(w.percentUsed)} of the ${noun} is used${reset}`
  })
  const soon = hot.filter((w) => w.hitsLimitAt !== null)
    .map((w) => ` At the current pace the ${labelsOf(w.kind)[2]} runs out at about ${words.dayTime(w.hitsLimitAt)}.`)
  const note = `Usage note (from Sticky AI Usage): ${said.join('. ')}.${soon.join('')} ` +
    'Prefer smaller steps and avoid parallel subagents unless they are needed.'
  return { note, memory: { prompts, notedAt: prompts, notedPercent: top } }
}

/** "at 4:30 PM" today, "Fri 3:30 PM" on another day. */
function resetWords(ms, words) {
  const said = words.dayTime(ms)
  return said === words.time(ms) ? `at ${said}` : said
}
