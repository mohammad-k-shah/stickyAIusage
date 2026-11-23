// node --test plugin/hooks/
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildRecord } from './feed.js'
import {
  PERMISSION_CONFIRM_MS, STATES, initialState, nextState, notificationEvent, recordFields, toolName,
} from './state.js'

const T0 = 1_794_000_000_000

/** Plays events one second apart from T0 (or at `at` when given). */
function play(events, from = initialState(T0)) {
  let state = from
  let at = T0
  for (const event of events) {
    at = event.at ?? at + 1000
    state = nextState(state, { ...event, at })
  }
  return state
}

test('a session starts as started', () => {
  assert.deepEqual(recordFields(initialState(T0)), { state: 'started', stateSince: T0, attention: null, lastTurnMs: null })
  assert.equal(nextState(play([{ type: 'turn.start' }]), { type: 'session.start', at: T0 + 9000 }).state, 'started')
})

test('a turn start is working, and so is the prompt that follows a wait', () => {
  const working = play([{ type: 'turn.start' }])
  assert.equal(working.state, 'working')
  assert.equal(working.attention, null)
  assert.equal(working.since, T0 + 1000)
})

test('a turn that ends with an answer is done, with how long it took', () => {
  const done = play([{ type: 'turn.start' }, { type: 'turn.complete', durationMs: 754_321.4 }])
  assert.equal(done.state, 'done')
  assert.equal(done.lastTurnMs, 754_321)
  // Without the engine's duration: from the turn's start.
  const measured = play([{ type: 'turn.start', at: T0 + 1000 }, { type: 'turn.complete', at: T0 + 61_000 }])
  assert.equal(measured.lastTurnMs, 60_000)
})

test('an interrupted turn is idle, not done', () => {
  assert.equal(play([{ type: 'turn.start' }, { type: 'turn.complete', aborted: true }]).state, 'idle')
})

test('a permission dialog is needs you, with the tool, until the tool has run', () => {
  const waiting = play([{ type: 'turn.start' }, { type: 'permission', tool: 'Bash' }])
  assert.equal(waiting.state, 'needs_you')
  assert.deepEqual(waiting.attention, { type: 'permission', tool: 'Bash' })
  assert.equal(waiting.since, T0 + 2000)
  // Another tool finishing (a parallel call) is no answer.
  assert.equal(nextState(waiting, { type: 'tool.done', tool: 'Read', at: T0 + 3000 }), waiting)
  const answered = nextState(waiting, { type: 'tool.done', tool: 'Bash', at: T0 + 4000 })
  assert.equal(answered.state, 'working')
  assert.equal(answered.attention, null)
})

test('the permission notification confirms the wait and keeps its start', () => {
  const waiting = play([{ type: 'turn.start' }, { type: 'permission', tool: 'Bash' }])
  const confirmed = nextState(waiting, { type: 'permission.confirmed', at: T0 + 8000 })
  assert.equal(confirmed.state, 'needs_you')
  assert.equal(confirmed.since, waiting.since)
  assert.equal(confirmed.confirmed, true)
  // Heard again: nothing new to write.
  assert.equal(nextState(confirmed, { type: 'permission.confirmed', at: T0 + 9000 }), confirmed)
  // A confirmed wait outlasts the timer.
  assert.equal(nextState(confirmed, { type: 'permission.timeout', episode: waiting.since, at: T0 + 14_000 }), confirmed)
})

test('a dialog Claude Code never called still open was answered: the timer ends it', () => {
  const waiting = play([{ type: 'turn.start' }, { type: 'permission', tool: 'Edit' }])
  const later = nextState(waiting, { type: 'permission.timeout', episode: waiting.since, at: waiting.since + PERMISSION_CONFIRM_MS })
  assert.equal(later.state, 'working')
  // The timer of an older episode does nothing to a newer one.
  const again = play([{ type: 'tool.done', tool: 'Edit', at: T0 + 20_000 }, { type: 'permission', tool: 'Edit', at: T0 + 21_000 }], waiting)
  assert.equal(again.since, T0 + 21_000)
  assert.equal(nextState(again, { type: 'permission.timeout', episode: waiting.since, at: T0 + 30_000 }), again)
  // The timer outlives Claude Code's five to six seconds, and stays under the widget's 20-second delay.
  assert.ok(PERMISSION_CONFIRM_MS > 6000 && PERMISSION_CONFIRM_MS < 20_000)
})

test('the notification alone (no PermissionRequest heard) is needs you too', () => {
  const waiting = play([{ type: 'turn.start' }, notificationEvent('permission_prompt')])
  assert.equal(waiting.state, 'needs_you')
  assert.deepEqual(waiting.attention, { type: 'permission', tool: null })
  // Any tool finishing answers a wait whose tool is unknown.
  assert.equal(nextState(waiting, { type: 'tool.done', tool: 'Bash', at: T0 + 9000 }).state, 'working')
})

test('a question waits until it is answered', () => {
  const asking = play([{ type: 'turn.start' }, { type: 'question' }])
  assert.equal(asking.state, 'needs_you')
  assert.deepEqual(asking.attention, { type: 'question' })
  assert.equal(nextState(asking, { type: 'question', at: T0 + 5000 }), asking)
  assert.equal(nextState(asking, { type: 'question.answered', at: T0 + 6000 }).state, 'working')
  // An answer with no question open changes nothing.
  const working = play([{ type: 'turn.start' }])
  assert.equal(nextState(working, { type: 'question.answered', at: T0 + 7000 }), working)
})

test('an MCP server asking for input waits until it is answered', () => {
  const asking = play([{ type: 'turn.start' }, notificationEvent('elicitation_dialog')])
  assert.deepEqual(asking.attention, { type: 'input' })
  assert.equal(play([notificationEvent('elicitation_url_dialog')]).attention.type, 'input')
  assert.equal(nextState(asking, { type: 'input.done', at: T0 + 9000 }).state, 'working')
})

test('a second ask while one is open is the same waiting episode', () => {
  const first = play([{ type: 'turn.start' }, { type: 'permission', tool: 'Bash' }])
  const second = nextState(first, { type: 'question', at: T0 + 9000 })
  assert.equal(second.since, first.since)
  assert.deepEqual(second.attention, { type: 'question' })
})

test('idle_prompt says done for a session whose turn end was missed, nothing otherwise', () => {
  assert.equal(play([{ type: 'turn.start' }, notificationEvent('idle_prompt')]).state, 'done')
  const done = play([{ type: 'turn.start' }, { type: 'turn.complete' }])
  assert.equal(nextState(done, { type: 'idle', at: T0 + 70_000 }), done)
  const waiting = play([{ type: 'turn.start' }, { type: 'question' }])
  assert.equal(nextState(waiting, { type: 'idle', at: T0 + 70_000 }), waiting)
})

test('notifications about other sessions or nothing in particular are ignored', () => {
  for (const type of ['agent_needs_input', 'agent_completed', 'auth_success', 'plugin_notification', 'computer_use_exit', undefined]) {
    assert.equal(notificationEvent(type), null, String(type))
  }
  assert.deepEqual(notificationEvent('worker_permission_prompt'), { type: 'permission.confirmed' })
})

test('a rate limit is rate limited, and the error end of the turn keeps it', () => {
  const limited = play([{ type: 'turn.start' }, { type: 'rate_limit' }])
  assert.equal(limited.state, 'rate_limited')
  assert.equal(nextState(limited, { type: 'turn.complete', error: true, at: T0 + 9000 }), limited)
  // Another API error ends as idle.
  assert.equal(play([{ type: 'turn.start' }, { type: 'turn.complete', error: true }]).state, 'idle')
  // The next turn works again.
  assert.equal(nextState(limited, { type: 'turn.start', at: T0 + 60_000 }).state, 'working')
})

test('the end of the session is ended, once', () => {
  const ended = play([{ type: 'turn.start' }, { type: 'question' }, { type: 'session.end' }])
  assert.equal(ended.state, 'ended')
  assert.equal(ended.attention, null)
  assert.equal(nextState(ended, { type: 'session.end', at: T0 + 99_000 }), ended)
})

test('a full episode: permission, answered, working, done', () => {
  const states = []
  let state = initialState(T0)
  for (const event of [
    { type: 'turn.start' }, { type: 'permission', tool: 'Bash' }, { type: 'permission.confirmed' },
    { type: 'tool.done', tool: 'Bash' }, { type: 'turn.complete' },
  ]) {
    state = nextState(state, { ...event, at: T0 + states.length * 1000 })
    states.push(state.state)
  }
  assert.deepEqual(states, ['working', 'needs_you', 'needs_you', 'working', 'done'])
  assert.ok(states.every((s) => STATES.includes(s)))
})

test('nothing changes on an event it does not know', () => {
  const working = play([{ type: 'turn.start' }])
  assert.equal(nextState(working, { type: 'something.new', at: T0 + 5 }), working)
  assert.equal(nextState(working, { type: 'turn.start', at: T0 + 6 }), working)
})

test('tool names keep only safe characters', () => {
  assert.equal(toolName('Bash'), 'Bash')
  assert.equal(toolName('mcp__github__create_issue'), 'mcp__github__create_issue')
  assert.equal(toolName('rm -rf /; echo "x"'), 'rm-rfechox')
  assert.equal(toolName('x'.repeat(200)).length, 64)
  assert.equal(toolName(''), null)
  assert.equal(toolName(42), null)
})

test('no prompt, path, command or message text reaches the record', () => {
  // What Claude Code's events carry beside the names the reducer reads.
  const secrets = ['C:\\Users\\me\\secret-project', 'rm -rf ~/work', 'Claude needs your permission to use Bash',
    'please refactor auth.ts', 'https://example.com/token=abc']
  let state = initialState(T0)
  for (const event of [
    { type: 'turn.start', text: secrets[3] },
    { type: 'permission', tool: 'Bash', input: { command: secrets[1] }, cwd: secrets[0] },
    { type: 'permission.confirmed', message: secrets[2] },
    { type: 'question', questions: [secrets[3]] },
    { type: 'input', url: secrets[4] },
  ]) state = nextState(state, { ...event, at: T0 + 1 })
  const record = buildRecord({ usage: null, sessionId: 's', cwd: null, model: null, surface: 'terminal', now: T0 + 2, ...recordFields(state) })
  const text = JSON.stringify(record)
  for (const secret of secrets) assert.equal(text.includes(secret), false, secret)
  assert.deepEqual(record.attention, { type: 'input' })
})
