// What a session is doing, for the widget's session lights: working, waiting
// for you, done. A pure reducer over normalized events (register.js turns
// Claude Code's events into them), so `node --test` checks every transition
// (state.test.mjs) and only the thin wiring depends on the engine's names.
//
// The record carries the state, since when, and for "needs_you" what kind of
// answer is waited for: never a prompt, a command, a path or a message, only
// the kind ("permission", "question", "input") and, for a permission, the
// tool's name.

/** The states a record can carry (the widget reads unknown ones as "working"). */
export const STATES = ['started', 'working', 'needs_you', 'done', 'idle', 'rate_limited', 'ended']

/**
 * Claude Code raises its own `permission_prompt` notification five to six
 * seconds after a permission dialog opens, and cancels it when the dialog is
 * answered (2.1.325). A dialog with no such notification after this long was
 * answered, or answered by a settings hook: the session is working again.
 */
export const PERMISSION_CONFIRM_MS = 12_000

/** A tool name kept in the record: letters, digits, `_`, `-`, `.`; at most 64. */
export function toolName(name) {
  if (typeof name !== 'string') return null
  const safe = name.replace(/[^A-Za-z0-9_.-]/g, '').slice(0, 64)
  return safe.length > 0 ? safe : null
}

/** The state of a session that has just started. */
export function initialState(at) {
  return { state: 'started', since: at ?? null, attention: null, confirmed: false, turnStartedAt: null, lastTurnMs: null }
}

/**
 * Claude Code's notification types (`classic.Notification`'s
 * `notification_type`) as events, or null for the ones that say nothing about
 * this session. `agent_needs_input` and `agent_completed` are about the
 * background sessions Claude Code's agent view supervises, not this one (each
 * of those writes its own record).
 */
export function notificationEvent(type) {
  switch (type) {
    case 'permission_prompt':
    case 'worker_permission_prompt':
      return { type: 'permission.confirmed' }
    case 'elicitation_dialog':
    case 'elicitation_url_dialog':
      return { type: 'input' }
    case 'idle_prompt':
      return { type: 'idle' }
    default:
      return null
  }
}

const waitingFor = (current, kind) => current.state === 'needs_you' && current.attention?.type === kind

function enter(current, state, at, extra = {}) {
  return { ...current, state, since: at, attention: null, confirmed: false, ...extra }
}

function waitFor(current, attention, at, confirmed) {
  // Another ask while one is open keeps the waiting time: it is one episode.
  const since = current.state === 'needs_you' ? current.since : at
  return { ...current, state: 'needs_you', since, attention, confirmed }
}

/**
 * The next state after `event` (`{ type, at, ... }`), or `current` itself when
 * nothing changes (so the caller knows not to write).
 */
export function nextState(current, event) {
  const at = event.at
  switch (event.type) {
    case 'session.start':
      return initialState(at)

    case 'turn.start': {
      // A turn of the main loop: a new prompt, or Claude going on.
      if (current.state === 'working' && current.attention === null) return current
      const started = current.state === 'working' || current.state === 'needs_you' ? current.turnStartedAt ?? at : at
      return enter(current, 'working', at, { turnStartedAt: started })
    }

    case 'turn.complete': {
      if (event.aborted) return enter(current, 'idle', at, { turnStartedAt: null })
      // An API error ends the turn after StopFailure said why: keep "rate limited".
      if (event.error) {
        return current.state === 'rate_limited' ? current : enter(current, 'idle', at, { turnStartedAt: null })
      }
      const startedAt = current.turnStartedAt
      const lastTurnMs = typeof event.durationMs === 'number' && Number.isFinite(event.durationMs)
        ? Math.max(0, Math.round(event.durationMs))
        : startedAt !== null ? Math.max(0, at - startedAt) : null
      return enter(current, 'done', at, { turnStartedAt: null, lastTurnMs })
    }

    case 'permission': {
      const attention = { type: 'permission', tool: toolName(event.tool) }
      if (waitingFor(current, 'permission') && current.attention.tool === attention.tool) return current
      return waitFor(current, attention, at, false)
    }

    case 'permission.confirmed': {
      // The dialog is still open after five seconds: really waiting.
      if (waitingFor(current, 'permission')) return current.confirmed ? current : { ...current, confirmed: true }
      return waitFor(current, { type: 'permission', tool: null }, at, true)
    }

    case 'permission.timeout':
      // Only the episode the timer was set for, and only if Claude Code never
      // said the dialog was still open.
      if (waitingFor(current, 'permission') && !current.confirmed && current.since === event.episode) {
        return enter(current, 'working', at)
      }
      return current

    case 'tool.done':
      // The tool that asked has run (or was refused): the answer came.
      if (waitingFor(current, 'permission') &&
        (current.attention.tool === null || current.attention.tool === toolName(event.tool))) {
        return enter(current, 'working', at)
      }
      return current

    case 'question':
      if (waitingFor(current, 'question')) return current
      return waitFor(current, { type: 'question' }, at, true)

    case 'question.answered':
      return waitingFor(current, 'question') ? enter(current, 'working', at) : current

    case 'input':
      if (waitingFor(current, 'input')) return current
      return waitFor(current, { type: 'input' }, at, true)

    case 'input.done':
      return waitingFor(current, 'input') ? enter(current, 'working', at) : current

    case 'idle':
      // "Claude is waiting for your input" after a finished turn is what
      // "done" already says; it corrects a session whose end was missed.
      return current.state === 'working' || current.state === 'started' ? enter(current, 'done', at) : current

    case 'rate_limit':
      return current.state === 'rate_limited' ? current : enter(current, 'rate_limited', at, { turnStartedAt: null })

    case 'session.end':
      return current.state === 'ended' ? current : enter(current, 'ended', at, { turnStartedAt: null })

    default:
      return current
  }
}

/** What the record carries of the state: no internal bookkeeping. */
export function recordFields(current) {
  return {
    state: current.state,
    stateSince: current.since,
    attention: current.attention,
    lastTurnMs: current.lastTurnMs,
  }
}
