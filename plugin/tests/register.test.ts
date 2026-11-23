// claude plugin test ./plugin: the hooks module against the engine itself.
// Each `on` hook here stands for the engine beneath the plugin; what the test
// leaves unanswered throws, as a missing noun would.
import type { On } from 'claude-code'
import { expect, mock, test } from 'claude-code/testing'

const NOW = Date.parse('2026-11-17T13:00:00Z')
const FIVE_RESET = '2026-11-17T16:30:00.000Z'
const WEEK_RESET = '2026-11-20T15:30:00.000Z'

const USAGE = {
  startedAt: NOW - 3_600_000,
  context: { tokens: 240000, window: 1000000, percent: 24 },
  rateLimits: [
    { kind: 'five_hour', percentUsed: 82, resetsAt: FIVE_RESET },
    { kind: 'seven_day', percentUsed: 14, resetsAt: WEEK_RESET },
  ],
  cost: { usd: 3.05 },
}

const BAND = {
  plugin: 'sticky-usage',
  component: 'AbovePrompt',
  requestId: 'band',
  props: {
    hasSurvey: false,
    isCollapsed: false,
    isWorking: false,
    maxRows: 10,
    bodyColumns: 80,
    scroll: { offset: 0, bodyRows: 10 },
    view: {},
  },
} as const

/** The engine beneath: the session's usage, a clock, a store, and a start. */
function world(on: On, usage: unknown = USAGE) {
  mock.clock(on, { now: NOW })
  mock.store(on)
  mock.env(on, { STICKYAIUSAGE_FEED_DIR: '/nowhere/feed' })
  on('session.usage', () => (usage instanceof Error ? Promise.reject(usage) : { value: usage }))
  on('session.surfaces', () => ({ value: [] }))
  on('fs.read', () => Promise.reject(new Error('ENOENT')))
  on('command.register', (_: unknown, e: { name: string }) => ({ value: { command: e.name } }))
  on('tool.register', (_: unknown, e: { name: string }) => ({ value: { tool: `mcp__sticky-usage__${e.name}` } }))
  on('session.start', (_: unknown, e: { cwd: string }) => ({ cwd: e.cwd }))
}

const start = ($: any) => $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })

test('/pace answers with text, and /pace json with JSON', { options: { feed: false } }, async ($, on) => {
  world(on)
  await start($)

  const { text } = await $.command.run({ command: 'pace', args: '' })
  expect(text).toMatch(/^5-hour: 82% used · resets /)
  expect(text).toMatch(/\nWeek: {3}14% used · resets /)
  expect(text).toMatch(/\nSession: context 24% · cost \$3\.05$/)

  const json = JSON.parse((await $.command.run({ command: 'pace', args: 'json' })).text)
  expect(json.windows.map((w: { kind: string }) => w.kind)).toEqual(['five_hour', 'seven_day'])
  expect(json.costUsd).toBe(3.05)
})

test('the usage_status tool returns valid JSON with the same numbers', { options: { feed: false } }, async ($, on) => {
  world(on)
  await start($)

  const called = await $.tool.call({ tool: 'mcp__sticky-usage__usage_status' })
  const status = JSON.parse(called.result as string)
  expect(status.schema).toBe(1)
  expect(status.windows[0].percentUsed).toBe(82)
  expect(status.summary).toMatch(/^5-hour: 82% used/)
})

test('a throwing usage() blocks nothing', { options: { feed: false } }, async ($, on) => {
  world(on, new Error('no usage on this host'))
  let entered: { text: string; context?: readonly string[] } | undefined
  on('prompt.submit', (_: unknown, e: { text: string; context?: readonly string[] }) => {
    entered = e
    return { text: e.text }
  })
  await start($)

  const { text } = await $.command.run({ command: 'pace', args: '' })
  expect(text).toBe('No plan limits reported for this session.')

  await $.prompt.submit({ text: 'hello' })
  expect(entered?.text).toBe('hello')
  expect(entered?.context).toBeUndefined()
})

test('near a limit one note rides beside the prompt, not again for ten prompts', { options: { feed: false } },
  async ($, on) => {
    world(on)
    const seen: (readonly string[] | undefined)[] = []
    on('prompt.submit', (_: unknown, e: { text: string; context?: readonly string[] }) => {
      seen.push(e.context)
      return { text: e.text }
    })
    await start($)
    for (let i = 0; i < 11; i++) await $.prompt.submit({ text: `prompt ${i}` })

    expect(seen[0]?.[0]).toMatch(/^Usage note \(from Sticky AI Usage\): 82% of the 5-hour window is used/)
    expect(seen.slice(1, 10).every((context) => context === undefined)).toBe(true)
    expect(seen[10]?.length).toBe(1)
  })

test('with contextNote off no note is added', { options: { feed: false, contextNote: false } }, async ($, on) => {
  world(on)
  let context: readonly string[] | undefined = ['unset']
  on('prompt.submit', (_: unknown, e: { text: string; context?: readonly string[] }) => {
    context = e.context
    return { text: e.text }
  })
  await start($)
  await $.prompt.submit({ text: 'hello' })
  expect(context).toBeUndefined()
})

test('the band shows at 82% in auto', { options: { feed: false } }, async ($, on) => {
  world(on)
  await start($)
  for (const surface of ['terminal', 'desktop', 'mobile'] as const) {
    const ui = await $.ui.mount({ ...BAND, surface })
    expect((await ui.find({ type: 'Text' }))?.text).toBe('◕ 5h 82% · week 14%')
    await ui.unmount()
  }
})

test('the band stays hidden below 50% in auto', { options: { feed: false } }, async ($, on) => {
  world(on, { ...USAGE, rateLimits: [{ kind: 'five_hour', percentUsed: 49, resetsAt: FIVE_RESET }] })
  on('ui.render', ($e: any, e: any) => {
    const { Text } = $e.ui.resolve(e)
    return h(Text, null, 'engine')
  })
  await start($)
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect((await ui.find({ type: 'Text' }))?.text).toBe('engine')
  await ui.unmount()
})

test('a band that fails to read the usage lets Claude Code draw its own', { options: { feed: false } }, async ($, on) => {
  // No clock on this host: reading the status throws inside the hook.
  mock.store(on)
  mock.env(on, { STICKYAIUSAGE_FEED_DIR: '/nowhere/feed' })
  on('clock.now', () => Promise.reject(new Error('no clock on this host')))
  on('session.usage', () => ({ value: USAGE }))
  on('session.surfaces', () => ({ value: [] }))
  on('fs.read', () => Promise.reject(new Error('ENOENT')))
  on('command.register', (_: unknown, e: { name: string }) => ({ value: { command: e.name } }))
  on('tool.register', (_: unknown, e: { name: string }) => ({ value: { tool: `mcp__sticky-usage__${e.name}` } }))
  on('session.start', (_: unknown, e: { cwd: string }) => ({ cwd: e.cwd }))
  on('ui.render', ($e: any, e: any) => {
    const { Text } = $e.ui.resolve(e)
    return h(Text, null, 'engine')
  })
  await start($)
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect((await ui.find({ type: 'Text' }))?.text).toBe('engine')
  await ui.unmount()
})

/** The feed beneath: every record the plugin writes, newest last. */
function feedWorld(on: On) {
  const clock = mock.clock(on, { now: NOW })
  mock.store(on)
  mock.env(on, { STICKYAIUSAGE_FEED_DIR: '/feed' })
  const records: any[] = []
  on('fs.write', (_: unknown, e: { path: string; text: string }) => {
    if (e.path.includes('claude-')) records.push(JSON.parse(e.text))
    return { value: undefined }
  })
  on('fs.exists', () => ({ value: false }))
  on('fs.read', () => Promise.reject(new Error('ENOENT')))
  on('session.usage', () => ({ value: USAGE }))
  on('session.surfaces', () => ({ value: ['terminal'] }))
  on('session.id', () => ({ value: 's1' }))
  on('session.cwd', () => ({ value: '/work/acme-webapp' }))
  on('session.model', () => ({ value: 'claude-haiku-5-5' }))
  on('session.version', () => ({ value: { version: '2.1.325', base: '2.1.325', builtAt: '2026-11-15' } }))
  on('command.register', (_: unknown, e: { name: string }) => ({ value: { command: e.name } }))
  on('tool.register', (_: unknown, e: { name: string }) => ({ value: { tool: `mcp__sticky-usage__${e.name}` } }))
  on('session.start', (_: unknown, e: { cwd: string }) => ({ cwd: e.cwd }))
  on('turn.start', (_: unknown, e: { turnId: string }) => ({ turnId: e.turnId }))
  on('turn.complete', () => ({ text: '' }))
  on('session.end', () => ({ sessionId: 's1' }))
  on('ui.log', () => ({ value: undefined }))
  // No settings hooks beneath: each classic event ends here.
  for (const event of ['PermissionRequest', 'PostToolUse', 'Notification', 'StopFailure'] as const) on(`classic.${event}`, () => ({}))
  const last = () => records[records.length - 1]
  return { clock, records, last }
}

test('a permission dialog turns the record to needs you at once, and the answer back to working',
  { options: { exposeTool: false } }, async ($, on) => {
    const { clock, last } = feedWorld(on)
    await start($)
    expect(last().state).toBe('started')

    await $.turn.start({ text: 'deploy it', turnId: 't1' })
    expect(last().state).toBe('working')

    await $.classic.PermissionRequest({ tool_name: 'Bash', tool_input: { command: 'rm -rf build' } })
    expect(last().state).toBe('needs_you')
    expect(last().attention).toEqual({ type: 'permission', tool: 'Bash' })
    const since = last().stateSince

    // Claude Code says the dialog is still open: the wait outlasts the timer.
    await clock.advance(6000)
    await $.classic.Notification({ message: 'Claude needs your permission to use Bash', notification_type: 'permission_prompt' })
    await clock.advance(20_000)
    expect(last().state).toBe('needs_you')
    expect(last().stateSince).toBe(since)

    await $.classic.PostToolUse({ tool_name: 'Bash', tool_input: {}, tool_response: {}, tool_use_id: 'u1' })
    expect(last().state).toBe('working')
    expect(last().attention).toBeNull()

    await clock.advance(60_000)
    await $.turn.complete({ answer: 'done', durationMs: 87_000, isAborted: false, turnId: 't1', reason: 'answer' })
    expect(last().state).toBe('done')
    expect(last().lastTurnMs).toBe(87_000)

    // Nothing of the prompt, the command or the notification's text in any record.
    const text = JSON.stringify(last())
    for (const words of ['deploy it', 'rm -rf build', 'needs your permission']) expect(text.includes(words)).toBe(false)
  })

test('a dialog answered before Claude Code calls it open is working again after the timer',
  { options: { exposeTool: false } }, async ($, on) => {
    const { clock, last } = feedWorld(on)
    await start($)
    await $.turn.start({ text: '', turnId: 't1' })
    await $.classic.PermissionRequest({ tool_name: 'Edit', tool_input: {} })
    expect(last().state).toBe('needs_you')
    await clock.advance(12_000)
    expect(last().state).toBe('working')
  })

test('a question waits for its answer; a rate limit and the end are said too', { options: { exposeTool: false } },
  async ($, on) => {
    const { clock, last } = feedWorld(on)
    let answer: (value: unknown) => void = () => {}
    on('tool.call', { tool: 'AskUserQuestion' }, () => new Promise((resolve) => { answer = resolve }))
    await start($)
    await $.turn.start({ text: '', turnId: 't1' })

    const asked = $.tool.call({ tool: 'AskUserQuestion', questions: [{ question: 'Which database?' }] } as any)
    await clock.settle()
    expect(last().state).toBe('needs_you')
    expect(last().attention).toEqual({ type: 'question' })
    answer({ result: 'Postgres' })
    await asked
    expect(last().state).toBe('working')

    await $.classic.StopFailure({ error: 'rate_limit' })
    expect(last().state).toBe('rate_limited')
    await $.turn.complete({ answer: '', durationMs: 1000, isAborted: false, turnId: 't1', reason: 'error' })
    expect(last().state).toBe('rate_limited')

    await $.session.end({ reason: 'prompt_input_exit' } as any)
    expect(last().state).toBe('ended')
  })

test('with band never the band never shows', { options: { feed: false, band: 'never' } }, async ($, on) => {
  world(on)
  on('ui.render', ($e: any, e: any) => {
    const { Text } = $e.ui.resolve(e)
    return h(Text, null, 'engine')
  })
  await start($)
  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  expect((await ui.find({ type: 'Text' }))?.text).toBe('engine')
  await ui.unmount()
})
