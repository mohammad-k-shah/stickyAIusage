// claude plugin test ./plugin: the budget guard against the engine itself.
// The test's own hooks stand for the engine beneath the plugin: the session's
// usage, a surface, the AskUserQuestion dialog (answered by the test) and the
// subagent that starts (`agent.spawn` beneath records what reached it).
import type { On } from 'claude-code'
import { expect, mock, test } from 'claude-code/testing'

const NOW = Date.parse('2026-11-17T13:00:00Z')
const FIVE_RESET = '2026-11-17T16:30:00.000Z'
const WEEK_RESET = '2026-11-20T15:30:00.000Z'
const SKIP_REASON = 'The user chose to save usage budget; continue without parallel subagents.'

const usage = (five: number, week = 14) => ({
  startedAt: NOW - 3_600_000,
  context: { tokens: 240000, window: 1000000, percent: 24 },
  rateLimits: [
    { kind: 'five_hour', percentUsed: five, resetsAt: FIVE_RESET },
    { kind: 'seven_day', percentUsed: week, resetsAt: WEEK_RESET },
  ],
  cost: { usd: 3.05 },
})

type Answer = string | Error | null
type Spawned = { prompt: string; model?: string }

/**
 * The engine beneath: usage at `five`%, a terminal (or none), a dialog that
 * answers `answer` (or is dismissed), and subagents that start.
 */
function world(on: On, { five = 92, surfaces = ['terminal'], answer = 'Run anyway' as Answer, ownClock = false } = {}) {
  // A test with its own clock hook leaves the mocked one out (one hook per noun).
  const clock = ownClock ? (undefined as unknown as ReturnType<typeof mock.clock>) : mock.clock(on, { now: NOW })
  mock.store(on)
  mock.env(on, { STICKYAIUSAGE_FEED_DIR: '/nowhere/feed' })
  on('session.usage', () => ({ value: usage(five) }))
  on('session.surfaces', () => ({ value: surfaces }))
  on('fs.read', () => Promise.reject(new Error('ENOENT')))
  on('command.register', (_: unknown, e: { name: string }) => ({ value: { command: e.name } }))
  on('tool.register', (_: unknown, e: { name: string }) => ({ value: { tool: `mcp__sticky-usage__${e.name}` } }))
  on('session.start', (_: unknown, e: { cwd: string }) => ({ cwd: e.cwd }))
  const questions: any[] = []
  if (answer !== null) on('tool.call', { tool: 'AskUserQuestion' }, (_: unknown, e: any) => {
    questions.push(e.questions[0])
    if (answer instanceof Error) return { deny: answer.message }
    return { result: { questions: e.questions, answers: { [e.questions[0].question]: answer } } }
  })
  const toasts: string[] = []
  on('ui.toast', (_: unknown, e: { text: string }) => {
    toasts.push(e.text)
    return { value: undefined }
  })
  const spawned: Spawned[] = []
  on('agent.spawn', (_: unknown, e: Spawned) => {
    spawned.push({ prompt: e.prompt, model: e.model })
    return { model: e.model ?? 'claude-opus-5-5', agentId: `a${spawned.length}` }
  })
  return { clock, questions, toasts, spawned }
}

const start = ($: any) => $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })
const fanOut = ($: any, count: number) =>
  Promise.all(Array.from({ length: count }, (_, i) => $.agent.spawn({ prompt: `task ${i + 1}`, description: `task ${i + 1}` })))

test('near the limit a fan-out of three asks once; "Use Haiku" runs all three on Haiku', { options: { feed: false } },
  async ($, on) => {
    const { questions, spawned } = world(on, { answer: 'Use Haiku for subagents' })
    await start($)
    const results = await fanOut($, 3)

    expect(questions.length).toBe(1)
    expect(questions[0].question).toMatch(/^About 8% of the 5-hour window is left \(resets .+\)\. Starting subagents now/)
    expect(questions[0].header).toBe('Budget')
    expect(questions[0].options.map((o: { label: string }) => o.label))
      .toEqual(['Run anyway', 'Use Haiku for subagents', 'Skip subagents'])
    expect(spawned.map((s) => s.model)).toEqual(['haiku', 'haiku', 'haiku'])
    expect(results.every((r: any) => r.deny === undefined)).toBe(true)
  })

test('"Run anyway" starts the subagents as they were', { options: { feed: false } }, async ($, on) => {
  const { questions, spawned } = world(on, { answer: 'Run anyway' })
  await start($)
  await fanOut($, 2)
  expect(questions.length).toBe(1)
  expect(spawned).toEqual([{ prompt: 'task 1', model: undefined }, { prompt: 'task 2', model: undefined }])
})

test('"Skip subagents" refuses them with the reason, and Claude goes on alone', { options: { feed: false } },
  async ($, on) => {
    const { questions, spawned } = world(on, { answer: 'Skip subagents' })
    await start($)
    const results = await fanOut($, 2)
    expect(questions.length).toBe(1)
    expect(spawned.length).toBe(0)
    expect(results.map((r: any) => r.deny)).toEqual([SKIP_REASON, SKIP_REASON])
  })

test('the answer holds for ten minutes, then the next fan-out is asked again', { options: { feed: false } },
  async ($, on) => {
    const { clock, questions, spawned } = world(on, { answer: 'Skip subagents' })
    await start($)
    await fanOut($, 1)
    await clock.advance(9 * 60_000)
    await fanOut($, 1)
    expect(questions.length).toBe(1)
    await clock.advance(60_000)
    await fanOut($, 1)
    expect(questions.length).toBe(2)
    expect(spawned.length).toBe(0)
  })

test('a dismissed question runs the subagent: the guard never refuses on its own', { options: { feed: false } },
  async ($, on) => {
    const { questions, spawned } = world(on, { answer: new Error('dismissed') })
    await start($)
    const [result] = await fanOut($, 1)
    expect(questions.length).toBe(1)
    expect(spawned).toEqual([{ prompt: 'task 1', model: undefined }])
    expect((result as any).deny).toBeUndefined()
  })

test('with nothing drawn (claude -p, the SDK) there is no question and no change', { options: { feed: false } },
  async ($, on) => {
    const { questions, toasts, spawned } = world(on, { five: 99, surfaces: [] })
    await start($)
    await fanOut($, 2)
    expect(questions.length).toBe(0)
    expect(toasts.length).toBe(0)
    expect(spawned.map((s) => s.model)).toEqual([undefined, undefined])
  })

test('the answer stands even when it cannot be remembered', { options: { feed: false } }, async ($, on) => {
  // A clock that answers until the dialog is answered, then fails.
  let failing = false
  on('clock.now', () => (failing ? Promise.reject(new Error('clock gone')) : { value: NOW }))
  on('ui.log', () => ({ value: undefined }))
  const { spawned } = world(on, { answer: null, ownClock: true })
  on('tool.call', { tool: 'AskUserQuestion' }, (_: unknown, e: any) => {
    failing = true
    return { result: { questions: e.questions, answers: { [e.questions[0].question]: 'Skip subagents' } } }
  })
  await start($)
  const [result] = await fanOut($, 1)
  expect((result as any).deny).toBe(SKIP_REASON)
  expect(spawned.length).toBe(0)
})

test('below the threshold nothing is asked', { options: { feed: false } }, async ($, on) => {
  const { questions, spawned } = world(on, { five: 43 })
  await start($)
  await fanOut($, 1)
  expect(questions.length).toBe(0)
  expect(spawned.length).toBe(1)
})

test('with guardThreshold 40 the question comes at 43%', { options: { feed: false, guardThreshold: 40 } },
  async ($, on) => {
    const { questions } = world(on, { five: 43 })
    await start($)
    await fanOut($, 1)
    expect(questions.length).toBe(1)
    expect(questions[0].question).toMatch(/^About 57% of the 5-hour window is left/)
  })

test('guard off: no question at 99%', { options: { feed: false, guard: 'off' } }, async ($, on) => {
  const { questions, spawned } = world(on, { five: 99 })
  await start($)
  await fanOut($, 2)
  expect(questions.length).toBe(0)
  expect(spawned.map((s) => s.model)).toEqual([undefined, undefined])
})

test('while the guard asks, the feed says the session waits for a question', { options: { exposeTool: false } },
  async ($, on) => {
    const records: any[] = []
    on('fs.write', (_: unknown, e: { path: string; text: string }) => {
      if (e.path.includes('claude-')) records.push(JSON.parse(e.text))
      return { value: undefined }
    })
    on('fs.exists', () => ({ value: false }))
    on('session.id', () => ({ value: 's1' }))
    on('session.cwd', () => ({ value: '/work/acme' }))
    on('session.model', () => ({ value: 'claude-opus-5-5' }))
    on('session.version', () => ({ value: { version: '2.1.325', base: '2.1.325', builtAt: '2026-11-15' } }))
    let answer: (value: unknown) => void = () => {}
    const { clock, spawned } = world(on, { answer: null })
    on('tool.call', { tool: 'AskUserQuestion' }, (_: unknown, e: any) => new Promise((resolve) => {
      answer = () => resolve({ result: { questions: e.questions, answers: { [e.questions[0].question]: 'Run anyway' } } })
    }))
    await start($)
    const pending = fanOut($, 1)
    await clock.settle()
    expect(records[records.length - 1].state).toBe('needs_you')
    expect(records[records.length - 1].attention).toEqual({ type: 'question' })
    answer(undefined)
    await pending
    expect(spawned.length).toBe(1)
    expect(records[records.length - 1].attention).toBeNull()
  })

test('downshift runs subagents on the guard model and says so once', { options: { feed: false, guard: 'downshift',
  guardModel: 'sonnet' } }, async ($, on) => {
  const { questions, toasts, spawned } = world(on)
  await start($)
  await fanOut($, 2)
  await fanOut($, 1)
  expect(questions.length).toBe(0)
  expect(spawned.map((s) => s.model)).toEqual(['sonnet', 'sonnet', 'sonnet'])
  expect(toasts).toEqual(['Budget guard: subagents run on Sonnet while the 5-hour window is at 92%'])
})
