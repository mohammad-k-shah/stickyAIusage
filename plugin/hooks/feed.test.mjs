// node --test plugin/hooks/*.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import {
  PLUGIN_VERSION, QUIET_REWRITE_MS, RECORD_SCHEMA, buildRecord, contentKey, continuesAfterEnd, feedFolder, inFolder,
  markerRecord, sessionFileName, shouldWrite,
} from './feed.js'

test('the feed folder on Windows is under LOCALAPPDATA', () => {
  assert.equal(
    feedFolder({ localAppData: 'C:\\Users\\me\\AppData\\Local', home: 'C:\\Users\\me', isMac: false }),
    'C:\\Users\\me\\AppData\\Local\\StickyAIusage\\feed')
  assert.equal(
    feedFolder({ localAppData: 'C:\\Users\\me\\AppData\\Local\\' }),
    'C:\\Users\\me\\AppData\\Local\\StickyAIusage\\feed')
})

test('the feed folder on macOS is under Application Support', () => {
  assert.equal(
    feedFolder({ home: '/Users/me', isMac: true }),
    '/Users/me/Library/Application Support/StickyAIusage/feed')
})

test('the feed folder elsewhere follows XDG_DATA_HOME, else ~/.local/share', () => {
  assert.equal(feedFolder({ home: '/home/me', xdgDataHome: '/data/me', isMac: false }), '/data/me/StickyAIusage/feed')
  assert.equal(feedFolder({ home: '/home/me', isMac: false }), '/home/me/.local/share/StickyAIusage/feed')
  assert.equal(feedFolder({ home: '/home/me', xdgDataHome: 'relative/dir' }), '/home/me/.local/share/StickyAIusage/feed')
})

test('STICKYAIUSAGE_FEED_DIR wins when it is a full path, and is ignored otherwise', () => {
  assert.equal(feedFolder({ override: 'D:\\tmp\\feed\\', localAppData: 'C:\\x' }), 'D:\\tmp\\feed')
  assert.equal(feedFolder({ override: '/tmp/feed', home: '/home/me' }), '/tmp/feed')
  assert.equal(feedFolder({ override: 'feed', localAppData: 'C:\\x' }), 'C:\\x\\StickyAIusage\\feed')
})

test('without a usable environment there is no folder', () => {
  assert.equal(feedFolder({}), null)
  assert.equal(feedFolder({ home: 'relative' }), null)
  assert.equal(feedFolder({ localAppData: 'relative' }), null)
})

test('paths in the folder keep its separator', () => {
  assert.equal(inFolder('C:\\a\\feed', 'x.json'), 'C:\\a\\feed\\x.json')
  assert.equal(inFolder('/a/feed', 'x.json'), '/a/feed/x.json')
})

test('session file names are safe whatever the id holds', () => {
  assert.equal(sessionFileName('9d5d7009-0d2b-4a35'), 'claude-9d5d7009-0d2b-4a35.json')
  assert.equal(sessionFileName('../../evil'), 'claude-______evil.json')
  assert.equal(sessionFileName(''), 'claude-unknown.json')
  assert.equal(sessionFileName(undefined), 'claude-unknown.json')
  assert.equal(sessionFileName('x'.repeat(300)).length, 'claude-.json'.length + 100)
})

const usage = {
  startedAt: 1794881631673,
  context: { tokens: 38097, window: 1000000, percent: 4, breakdown: { big: true } },
  rateLimits: [
    { kind: 'five_hour', percentUsed: 85, resetsAt: '2026-11-17T05:10:00.000Z' },
    { kind: 'seven_day', percentUsed: 61, resetsAt: '2026-11-20T05:00:00.000Z' },
  ],
  cost: { usd: 0.00245073 },
}

const base = {
  usage, sessionId: 's1', cwd: 'C:\\work', model: 'claude-haiku-5-5', surface: 'terminal',
  claudeCodeVersion: '2.1.325', now: 1794881636736, rateLimitsAt: 1794881635672, state: 'idle', stateSince: 1794881636000,
}

test('a record carries the schema, the session and the limits exactly as Claude Code gave them', () => {
  const record = buildRecord(base)
  assert.equal(record.schema, RECORD_SCHEMA)
  assert.equal(record.pluginVersion, PLUGIN_VERSION)
  assert.equal(record.sessionId, 's1')
  assert.equal(record.observedAt, 1794881636736)
  assert.equal(record.rateLimitsAt, 1794881635672)
  assert.equal(record.startedAt, 1794881631673)
  assert.deepEqual(record.rateLimits, usage.rateLimits)
  assert.deepEqual(record.cost, usage.cost)
  assert.deepEqual(record.context, { tokens: 38097, window: 1000000, percent: 4 })
  assert.equal(record.state, 'idle')
  assert.equal(record.attention, null)
  // What the widget reads is plain JSON.
  assert.deepEqual(JSON.parse(JSON.stringify(record)), record)
})

test('without usage() the record still names the session, with null limits', () => {
  const record = buildRecord({ ...base, usage: null, rateLimitsAt: null })
  assert.equal(record.rateLimits, null)
  assert.equal(record.cost, null)
  assert.equal(record.context, null)
  assert.equal(record.startedAt, null)
  assert.equal(record.sessionId, 's1')
})

test('a record keeps only the kind of wait and the tool name', () => {
  const waiting = buildRecord({ ...base, state: 'needs_you', attention: { type: 'permission', tool: 'Bash', command: 'rm -rf /', cwd: 'C:\\x' }, lastTurnMs: 61000 })
  assert.deepEqual(waiting.attention, { type: 'permission', tool: 'Bash' })
  assert.equal(waiting.lastTurnMs, 61000)
  assert.deepEqual(buildRecord({ ...base, attention: { type: 'question' } }).attention, { type: 'question' })
  for (const attention of [null, undefined, 'permission', { tool: 'Bash' }, { type: 7 }]) {
    assert.equal(buildRecord({ ...base, attention }).attention, null)
  }
  assert.equal(buildRecord({ ...base, lastTurnMs: Number.NaN }).lastTurnMs, null)
})

test('a record whose state has no time yet uses the write time', () => {
  assert.equal(buildRecord({ ...base, stateSince: null }).stateSince, base.now)
})

test('the content key ignores only the write time', () => {
  const a = buildRecord(base)
  const b = buildRecord({ ...base, now: base.now + 60_000 })
  const c = buildRecord({ ...base, usage: { ...usage, rateLimits: [{ kind: 'five_hour', percentUsed: 86 }] } })
  assert.equal(contentKey(a), contentKey(b))
  assert.notEqual(contentKey(a), contentKey(c))
})

test('a record is written when forced, changed, or about to look stale', () => {
  const now = 1_000_000
  assert.equal(shouldWrite({ force: true, key: 'k', lastKey: 'k', now, lastWrittenAt: now }), true)
  assert.equal(shouldWrite({ force: false, key: 'k2', lastKey: 'k', now, lastWrittenAt: now }), true)
  assert.equal(shouldWrite({ force: false, key: 'k', lastKey: 'k', now, lastWrittenAt: now - 60_000 }), false)
  assert.equal(shouldWrite({ force: false, key: 'k', lastKey: 'k', now, lastWrittenAt: now - QUIET_REWRITE_MS }), true)
  assert.equal(shouldWrite({ force: false, key: 'k', lastKey: null, now, lastWrittenAt: 0 }), true)
  // Rewrites stay inside the widget's three-minute "live" window.
  assert.ok(QUIET_REWRITE_MS + 60_000 <= 3 * 60_000)
})

test('only a /clear goes on after the session ends', () => {
  assert.equal(continuesAfterEnd('clear'), true)
  for (const reason of ['logout', 'prompt_input_exit', 'other', 'resume', undefined]) {
    assert.equal(continuesAfterEnd(reason), false)
  }
})

test('the marker names the plugin and Claude Code versions', () => {
  assert.deepEqual(markerRecord({ claudeCodeVersion: '2.1.325', surface: 'desktop', now: 5 }),
    { schema: RECORD_SCHEMA, pluginVersion: PLUGIN_VERSION, claudeCodeVersion: '2.1.325', surface: 'desktop', observedAt: 5 })
})

test('the version in the code matches the manifest', () => {
  const manifest = JSON.parse(readFileSync(fileURLToPath(new URL('../.claude-plugin/plugin.json', import.meta.url)), 'utf8'))
  assert.equal(manifest.version, PLUGIN_VERSION)
})

test('the hooks module makes no network, process or model calls', () => {
  // Every file the module loads. The one tool call allowed is declaring
  // usage_status; never running a tool.
  for (const file of ['./register.js', './feed.js', './status.js', './pace.js', './state.js', './guard.js']) {
    const source = readFileSync(fileURLToPath(new URL(file, import.meta.url)), 'utf8')
    for (const noun of ['$.http', '$.process', '$.model', '$.agent', '$.mcp']) {
      assert.equal(source.includes(noun), false, `${noun} found in ${file}`)
    }
    const tools = source.match(/$.tool.w+/g) ?? []
    assert.deepEqual(tools.filter((call) => call !== '$.tool.register'), [], `a tool call in ${file}`)
  }
})
