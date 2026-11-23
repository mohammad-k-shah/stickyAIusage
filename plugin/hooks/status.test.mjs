// node --test: the in-agent surface's pure helpers (status.js): samples,
// the widget's forecast file, the status, `/pace` lines (12- and 24-hour),
// the JSON, the band rule and the context note's throttling.
import assert from 'node:assert/strict'
import { test } from 'node:test'

import { HOUR, MINUTE } from './pace.js'
import {
  BRIDGE_FRESH_MS, MAX_SAMPLES, NO_LIMITS, addSample, bandLine, bandShows, buildStatus, clock, contextNote,
  LATEST_MAX_AGE_MS, isoLocal, labelsOf, latestLimits, paceJson, paceLines, prefersHour12, readBridge, samplesKey, samplesOf, toMs,
} from './status.js'

// Tuesday 17 November 2026, 13:00 in the zone the tests format in (UTC).
const NOW = Date.parse('2026-11-17T13:00:00Z')
const FIVE_RESET = Date.parse('2026-11-17T16:30:00Z')
const WEEK_RESET = Date.parse('2026-11-20T15:30:00Z')

const utcParts = (ms) => {
  const d = new Date(ms)
  return { year: d.getUTCFullYear(), month: d.getUTCMonth(), day: d.getUTCDate(), weekday: d.getUTCDay(),
    hours: d.getUTCHours(), minutes: d.getUTCMinutes() }
}
const words12 = clock({ now: NOW, hour12: true, parts: utcParts })
const words24 = clock({ now: NOW, hour12: false, parts: utcParts })
const isoUtc = (ms) => isoLocal(ms, 0)

/** What `$.session.usage()` answered in the 2.1.325 spike, with these figures. */
function usage({ five = 43, week = 14, context = { tokens: 240000, window: 1000000, percent: 24 }, cost = 3.05 } = {}) {
  const rateLimits = []
  if (five !== null) rateLimits.push({ kind: 'five_hour', percentUsed: five, resetsAt: new Date(FIVE_RESET).toISOString() })
  if (week !== null) rateLimits.push({ kind: 'seven_day', percentUsed: week, resetsAt: new Date(WEEK_RESET).toISOString() })
  return { startedAt: NOW - HOUR, context, rateLimits, cost: cost === null ? undefined : { usd: cost } }
}

/** forecast.json as the widget writes it. */
function bridgeText({ updatedAt = NOW - 30_000, hitsLimitAt = Date.parse('2026-11-17T16:12:00Z'), resetsAt = FIVE_RESET,
  confidence = 'high', schema = 1 } = {}) {
  return JSON.stringify({ schema, updatedAt: new Date(updatedAt).toISOString(), windows: [
    { kind: 'five_hour', percentUsed: 43, resetsAt: new Date(resetsAt).toISOString(),
      hitsLimitAt: hitsLimitAt === null ? null : new Date(hitsLimitAt).toISOString(), projectedAtReset: 120.4, confidence },
    { kind: 'seven_day', percentUsed: 14, resetsAt: new Date(WEEK_RESET).toISOString(), hitsLimitAt: null,
      projectedAtReset: null, confidence: 'none' },
  ] })
}

const statusOf = (input = {}) => buildStatus({ usage: usage(input), now: NOW, bridge: readBridge(bridgeText(), NOW),
  samples: {}, ...input.override })

test('reset times are read as ISO text, seconds or milliseconds', () => {
  assert.equal(toMs('2026-11-17T16:30:00.000Z'), FIVE_RESET)
  assert.equal(toMs(FIVE_RESET / 1000), FIVE_RESET)
  assert.equal(toMs(FIVE_RESET), FIVE_RESET)
  for (const bad of ['soon', '', null, undefined, 0, -5, Number.NaN, {}]) assert.equal(toMs(bad), null)
})

test('sample keys take only plain window names', () => {
  assert.equal(samplesKey('five_hour'), 'samples:five_hour')
  assert.equal(samplesKey('../x'), null)
  assert.equal(samplesKey(''), null)
  assert.equal(samplesKey(7), null)
})

test('a new reading is added; an unchanged one only after five minutes', () => {
  let rows = addSample(undefined, 'five_hour', { at: NOW, percent: 40, resetsAt: FIVE_RESET })
  assert.deepEqual(rows, [[NOW, 40, FIVE_RESET]])
  const same = addSample(rows, 'five_hour', { at: NOW + 4 * MINUTE, percent: 40, resetsAt: FIVE_RESET })
  assert.equal(same, rows, 'nothing to write')
  rows = addSample(rows, 'five_hour', { at: NOW + 5 * MINUTE, percent: 40, resetsAt: FIVE_RESET })
  assert.equal(rows.length, 2)
  rows = addSample(rows, 'five_hour', { at: NOW + 6 * MINUTE, percent: 41, resetsAt: FIVE_RESET })
  assert.equal(rows.length, 3)
})

test('readings seconds apart move the newest forward, and a new window is a new series', () => {
  let rows = addSample([], 'five_hour', { at: NOW, percent: 40, resetsAt: FIVE_RESET })
  rows = addSample(rows, 'five_hour', { at: NOW + 10_000, percent: 41, resetsAt: FIVE_RESET })
  rows = addSample(rows, 'five_hour', { at: NOW + 15_000, percent: 42, resetsAt: FIVE_RESET })
  assert.deepEqual(rows, [[NOW, 40, FIVE_RESET], [NOW + 15_000, 42, FIVE_RESET]])
  rows = addSample(rows, 'five_hour', { at: NOW + 16_000, percent: 1, resetsAt: FIVE_RESET + 5 * HOUR })
  assert.equal(rows.length, 3, 'another reset keeps the old series')
})

test('a steady stream of weekly readings builds a series the pace can use', () => {
  // Found live: readings 6 minutes apart kept replacing each other, so the
  // weekly series never grew past one reading.
  const weekReset = NOW + 3 * 24 * HOUR
  let rows = []
  for (let i = 0; i <= 40; i++) {
    rows = addSample(rows, 'seven_day', { at: NOW + i * 6 * MINUTE, percent: 35 + i * 0.1, resetsAt: weekReset })
  }
  // 4 hours of readings 6 minutes apart. The newest moves forward while it is
  // under 14.4 minutes past the one before it: 0, 6 → 0, 12 → 0, 12, 18 →
  // 0, 12, 24 → …, so one is kept every 12 minutes: 240 / 12 + 1 = 21.
  assert.equal(rows.length, 21)
  assert.equal(rows[0][0], NOW)
  assert.equal(rows[rows.length - 1][0], NOW + 240 * MINUTE)
  for (let i = 2; i < rows.length; i++) assert.ok(rows[i][0] - rows[i - 2][0] >= 14.4 * MINUTE)
})

test('a clock set back drops the readings from its "future"', () => {
  let rows = []
  for (let i = 0; i < 5; i++) rows = addSample(rows, 'five_hour', { at: NOW + i * MINUTE, percent: 40 + i, resetsAt: FIVE_RESET })
  rows = addSample(rows, 'five_hour', { at: NOW + 90_000, percent: 41, resetsAt: FIVE_RESET })
  assert.deepEqual(rows.map((r) => r[0]), [NOW, NOW + MINUTE, NOW + 90_000])
})

test('the newest 200 readings are kept', () => {
  let rows = []
  for (let i = 0; i < 250; i++) rows = addSample(rows, 'five_hour', { at: NOW + i * MINUTE, percent: i / 10, resetsAt: FIVE_RESET })
  assert.equal(rows.length, MAX_SAMPLES)
  assert.equal(rows[rows.length - 1][0], NOW + 249 * MINUTE)
})

test('a malformed reading or store is not added to, nor thrown on', () => {
  const rows = [[NOW, 40, FIVE_RESET]]
  assert.equal(addSample(rows, 'five_hour', { at: 'x', percent: 1 }), rows)
  assert.equal(addSample(rows, 'five_hour', null), rows)
  assert.deepEqual(addSample('junk', 'five_hour', { at: NOW, percent: 1, resetsAt: null }), [[NOW, 1, null]])
  assert.deepEqual(samplesOf([[NOW, 40, FIVE_RESET], [NOW, 'x', null], 'row', [NOW, 1, 'soon'], [NOW, 2, null]]),
    [{ at: NOW, percent: 40, resetsAt: FIVE_RESET }, { at: NOW, percent: 2, resetsAt: null }])
  assert.deepEqual(samplesOf({ not: 'rows' }), [])
})

test('the widget forecast file is used only while it is current', () => {
  const fresh = readBridge(bridgeText(), NOW)
  assert.equal(fresh.five_hour.hitsLimitAt, Date.parse('2026-11-17T16:12:00Z'))
  assert.equal(fresh.five_hour.confidence, 'high')
  assert.equal(fresh.seven_day.hitsLimitAt, null)
  assert.notEqual(readBridge(bridgeText({ updatedAt: NOW - BRIDGE_FRESH_MS }), NOW), null)
  assert.equal(readBridge(bridgeText({ updatedAt: NOW - BRIDGE_FRESH_MS - 1 }), NOW), null, 'widget closed')
  assert.equal(readBridge(bridgeText({ updatedAt: NOW + 10 * MINUTE }), NOW), null, 'from the future')
  assert.equal(readBridge(bridgeText({ schema: 2 }), NOW), null)
  for (const bad of ['', '{', 'null', '{"schema":1}', undefined]) assert.equal(readBridge(bad, NOW), null)
})

test('/pace prints the three lines of the design (12-hour)', () => {
  assert.equal(paceLines(statusOf(), words12), [
    '5-hour: 43% used · resets 4:30 PM · at this pace: limit ~4:12 PM',
    'Week:   14% used · resets Fri 3:30 PM · under an even pace',
    'Session: context 24% · cost $3.05',
  ].join('\n'))
})

test('/pace follows a 24-hour clock', () => {
  assert.equal(paceLines(statusOf(), words24).split('\n')[0], '5-hour: 43% used · resets 16:30 · at this pace: limit ~16:12')
  assert.equal(paceLines(statusOf(), words24).split('\n')[1], 'Week:   14% used · resets Fri 15:30 · under an even pace')
})

test('without limits /pace says so and still gives the session line', () => {
  const status = buildStatus({ usage: usage({ five: null, week: null }), now: NOW, bridge: null, samples: {} })
  assert.equal(paceLines(status, words12), `${NO_LIMITS}\nSession: context 24% · cost $3.05`)
  const bare = buildStatus({ usage: null, now: NOW, bridge: null, samples: {} })
  assert.equal(paceLines(bare, words12), NO_LIMITS)
})

test('a missing window is left out and the rest still line up', () => {
  const status = buildStatus({ usage: usage({ five: null }), now: NOW, bridge: null, samples: {} })
  assert.equal(paceLines(status, words12).split('\n')[0], 'Week: 14% used · resets Fri 3:30 PM · under an even pace')
})

test('over 100% the line says the limit is reached, with no forecast', () => {
  const status = buildStatus({ usage: { rateLimits: [{ kind: 'spend_limit', percentUsed: 112.5,
    resetsAt: new Date(WEEK_RESET).toISOString() }] }, now: NOW, bridge: null, samples: {} })
  assert.equal(paceLines(status, words12), 'Spend limit: 113% used · resets Fri 3:30 PM · limit reached')
  assert.equal(status.windows[0].hitsLimitAt, null)
})

test('a window whose reset passed reads as started over', () => {
  const status = buildStatus({ usage: { rateLimits: [{ kind: 'five_hour', percentUsed: 97,
    resetsAt: new Date(NOW - MINUTE).toISOString() }] }, now: NOW, bridge: readBridge(bridgeText(), NOW), samples: {} })
  assert.equal(paceLines(status, words12), '5-hour: 0% used · reset 12:59 PM, starts again with your next message')
})

test('reset and limit times far off name the day, then the date', () => {
  assert.equal(words12.dayTime(NOW + 2 * HOUR), '3:00 PM')
  assert.equal(words12.dayTime(Date.parse('2026-11-18T09:05:00Z')), 'Wed 9:05 AM')
  assert.equal(words12.dayTime(Date.parse('2026-11-25T00:00:00Z')), 'Nov 25 12:00 AM')
  assert.equal(words24.time(Date.parse('2026-11-18T09:05:00Z')), '09:05')
  assert.equal(typeof prefersHour12(), 'boolean')
})

test('the widget forecast wins over the plugin own while it is current', () => {
  // Samples that put the limit at 15:00 by the plugin's own pace.
  const samples = { five_hour: Array.from({ length: 7 }, (_, i) => ({ at: NOW - (30 - 5 * i) * MINUTE, percent: 25 + 3 * i,
    resetsAt: FIVE_RESET })) }
  const withWidget = buildStatus({ usage: usage(), now: NOW, bridge: readBridge(bridgeText(), NOW), samples })
  assert.equal(withWidget.windows[0].forecastFrom, 'widget')
  assert.equal(withWidget.windows[0].hitsLimitAt, Date.parse('2026-11-17T16:12:00Z'))

  const closed = buildStatus({ usage: usage(), now: NOW, bridge: readBridge(bridgeText({ updatedAt: NOW - 3 * MINUTE }), NOW),
    samples })
  assert.equal(closed.windows[0].forecastFrom, 'plugin')
  // 3 points per 5 minutes = 36/h from 43% at 13:00: 57 left = 95 minutes, 14:35.
  assert.ok(Math.abs(closed.windows[0].hitsLimitAt - Date.parse('2026-11-17T14:35:00Z')) <= 2 * MINUTE)

  const otherWindow = buildStatus({ usage: usage(), now: NOW, bridge: readBridge(bridgeText({ resetsAt: FIVE_RESET + HOUR }), NOW),
    samples })
  assert.equal(otherWindow.windows[0].forecastFrom, 'plugin', 'a forecast of another window is not used')
})

test('without either forecast a window has only its even pace', () => {
  const status = buildStatus({ usage: usage(), now: NOW, bridge: null, samples: {} })
  assert.equal(status.windows[0].forecastFrom, null)
  assert.equal(status.windows[0].hitsLimitAt, null)
  assert.equal(paceLines(status, words12).split('\n')[0], '5-hour: 43% used · resets 4:30 PM')
  // 5-hour: 1.5 h of 5 have passed (30%): 43 − 30 = +13.
  assert.ok(Math.abs(status.windows[0].aheadOfEvenPace - 13) < 0.01)
})

test('a widget forecast without confidence shows nothing', () => {
  const bridge = readBridge(bridgeText({ confidence: 'none' }), NOW)
  const status = buildStatus({ usage: usage(), now: NOW, bridge, samples: {} })
  assert.equal(status.windows[0].hitsLimitAt, null)
  assert.equal(status.windows[0].projectedAtReset, null)
  assert.equal(status.windows[0].forecastFrom, null, 'nothing was forecast, so nothing is credited')
})

test('context comes from the percent, or from tokens and window', () => {
  assert.equal(statusOf().context.percent, 24)
  const derived = buildStatus({ usage: usage({ context: { tokens: 50000, window: 200000 } }), now: NOW, bridge: null, samples: {} })
  assert.equal(derived.context.percent, 25)
  const none = buildStatus({ usage: usage({ context: { window: 1000000 }, cost: null }), now: NOW, bridge: null, samples: {} })
  assert.equal(none.context, null)
  assert.equal(none.costUsd, null)
})

test('unknown kinds keep their name and broken limits are skipped', () => {
  const status = buildStatus({ usage: { rateLimits: [null, { kind: 7 }, { kind: 'five_hour' },
    { kind: 'seven_day_extra', percentUsed: 5 }, { kind: 'five_hour', percentUsed: 9 }] }, now: NOW, bridge: null, samples: {} })
  assert.deepEqual(status.windows.map((w) => w.label), ['5-hour', 'Seven day extra'])
  assert.deepEqual(labelsOf('seven_day_opus'), ['Week (Opus)', 'opus week', 'weekly Opus window'])
})

test('/pace json is the same status as JSON, with local times', () => {
  const json = paceJson(statusOf(), words12, isoUtc)
  const again = JSON.parse(JSON.stringify(json))
  assert.equal(again.schema, 1)
  assert.equal(again.windows[0].kind, 'five_hour')
  assert.equal(again.windows[0].percentUsed, 43)
  assert.equal(again.windows[0].resetsAt, '2026-11-17T16:30:00Z')
  assert.equal(again.windows[0].hitsLimitAt, '2026-11-17T16:12:00Z')
  assert.equal(again.windows[0].confidence, 'high')
  assert.equal(again.windows[1].hitsLimitAt, null)
  assert.equal(again.costUsd, 3.05)
  assert.equal(again.summary, paceLines(statusOf(), words12))
  const empty = paceJson(buildStatus({ usage: null, now: NOW, bridge: null, samples: {} }), words12, isoUtc)
  assert.deepEqual(empty.windows, [])
  assert.equal(empty.notes, NO_LIMITS)
})

test('local ISO times carry their offset', () => {
  assert.equal(isoLocal(FIVE_RESET, 330), '2026-11-17T22:00:00+05:30')
  assert.equal(isoLocal(FIVE_RESET, -300), '2026-11-17T11:30:00-05:00')
  assert.equal(isoLocal(FIVE_RESET, 0), '2026-11-17T16:30:00Z')
  assert.equal(isoLocal(null), null)
  assert.match(isoLocal(FIVE_RESET), /^2026-11-1\dT\d\d:\d\d:00(Z|[+-]\d\d:\d\d)$/)
})

test('the band shows from 50% in auto, always with any limit, never with never', () => {
  const at = (five, week = 10) => buildStatus({ usage: usage({ five, week }), now: NOW, bridge: null, samples: {} })
  assert.equal(bandShows(at(50), 'auto'), true)
  assert.equal(bandShows(at(49.9), 'auto'), false)
  assert.equal(bandShows(at(10, 50), 'auto'), true, 'the weekly window counts too')
  assert.equal(bandShows(statusOf({ five: 30 }), 'auto'), true, 'a forecast limit shows it below 50%')
  assert.equal(bandShows(at(10), 'always'), true)
  assert.equal(bandShows(at(99), 'never'), false)
  const none = buildStatus({ usage: null, now: NOW, bridge: null, samples: {} })
  assert.equal(bandShows(none, 'always'), false, 'no limits, no band')
})

test('the band is one line with a ring, the limit time and a level', () => {
  const band = bandLine(statusOf({ five: 78, week: 31 }), words12)
  assert.equal(band.text, '◕ 5h 78% · limit ~4:12 PM · week 31%')
  assert.equal(band.level, null, 'the limit is more than an hour away')
  const close = buildStatus({ usage: usage({ five: 78 }), now: NOW,
    bridge: readBridge(bridgeText({ hitsLimitAt: NOW + 20 * MINUTE }), NOW), samples: {} })
  assert.equal(bandLine(close, words12).level, 'error')
  const hour = buildStatus({ usage: usage({ five: 60 }), now: NOW,
    bridge: readBridge(bridgeText({ hitsLimitAt: NOW + 50 * MINUTE }), NOW), samples: {} })
  assert.equal(bandLine(hour, words12).level, 'warning')
  const quiet = buildStatus({ usage: usage({ five: 12, week: 3 }), now: NOW, bridge: null, samples: {} })
  assert.equal(bandLine(quiet, words12).text, '○ 5h 12% · week 3%')
  const full = buildStatus({ usage: usage({ five: 100 }), now: NOW, bridge: null, samples: {} })
  assert.equal(bandLine(full, words12).text, '● 5h 100% · limit reached · week 14%')
  assert.equal(bandLine(full, words12).level, 'error')
})

const fresh = { prompts: 0, notedAt: null, notedPercent: null }

test('one context note at the threshold, then none for ten prompts', () => {
  const hot = statusOf({ five: 82 })
  let memory = fresh
  const notes = []
  for (let prompt = 1; prompt <= 21; prompt++) {
    const result = contextNote({ status: hot, threshold: 80, memory, words: words12 })
    memory = result.memory
    if (result.note) notes.push(prompt)
  }
  assert.deepEqual(notes, [1, 11, 21])
})

test('the note says what is used, when it resets and the forecast; never below the threshold', () => {
  const { note } = contextNote({ status: statusOf({ five: 82 }), threshold: 80, memory: fresh, words: words12 })
  assert.equal(note, 'Usage note (from Sticky AI Usage): 82% of the 5-hour window is used; it resets at 4:30 PM. ' +
    'At the current pace the 5-hour window runs out at about 4:12 PM. ' +
    'Prefer smaller steps and avoid parallel subagents unless they are needed.')
  assert.equal(contextNote({ status: statusOf({ five: 79.9 }), threshold: 80, memory: fresh, words: words12 }).note, null)
  const week = contextNote({ status: statusOf({ five: 10, week: 91 }), threshold: 80, memory: fresh, words: words12 })
  assert.match(week.note, /91% of the weekly window is used; it resets Fri 3:30 PM\./)
  const out = contextNote({ status: statusOf({ five: 100 }), threshold: 80, memory: fresh, words: words12 })
  assert.match(out.note, /the 5-hour window is used up; it resets at 4:30 PM\. Prefer/)
})

test('a move of five points brings the note back early', () => {
  let { memory } = contextNote({ status: statusOf({ five: 82 }), threshold: 80, memory: fresh, words: words12 })
  ;({ memory } = contextNote({ status: statusOf({ five: 84 }), threshold: 80, memory, words: words12 }))
  const moved = contextNote({ status: statusOf({ five: 87 }), threshold: 80, memory, words: words12 })
  assert.notEqual(moved.note, null)
  assert.equal(moved.memory.notedPercent, 87)
  assert.equal(moved.memory.notedAt, 3)
})

test('a session with no limits of its own yet speaks from the newest readings kept, saying when', () => {
  // Seen on a Mac: /pace as the first thing in a new session said "No plan limits reported".
  const samples = {
    five_hour: [{ at: NOW - 20 * MINUTE, percent: 40, resetsAt: FIVE_RESET }, { at: NOW - 12 * MINUTE, percent: 52, resetsAt: FIVE_RESET }],
    seven_day: [{ at: NOW - 12 * MINUTE, percent: 82, resetsAt: WEEK_RESET }],
  }
  const latest = latestLimits(samples, NOW)
  assert.deepEqual(latest.map((l) => [l.kind, l.percentUsed, l.asOf]), [['five_hour', 52, NOW - 12 * MINUTE], ['seven_day', 82, NOW - 12 * MINUTE]])

  const status = buildStatus({ usage: { rateLimits: [] }, now: NOW, bridge: null, samples, latest })
  const lines = paceLines(status, words12).split('\n')
  assert.match(lines[0], /^5-hour: 52% used \(as of 12:48 PM\) · resets 4:30 PM/)
  assert.match(lines[1], /^Week:\s+82% used \(as of 12:48 PM\) · resets Fri 3:30 PM/)
  const json = paceJson(status, words12, (ms) => (ms === null ? null : new Date(ms).toISOString()))
  assert.equal(json.windows[0].asOf, new Date(NOW - 12 * MINUTE).toISOString())

  // The session's own limits win; old or future readings are not offered.
  const own = buildStatus({ usage: { rateLimits: [{ kind: 'five_hour', percentUsed: 60, resetsAt: new Date(FIVE_RESET).toISOString() }] },
    now: NOW, bridge: null, samples, latest })
  assert.deepEqual(own.windows.map((w) => [w.kind, w.percentUsed, w.asOf]), [['five_hour', 60, null]])
  assert.deepEqual(latestLimits({ five_hour: [{ at: NOW - LATEST_MAX_AGE_MS - MINUTE, percent: 9, resetsAt: null }] }, NOW), [])
  assert.deepEqual(latestLimits({ five_hour: [{ at: NOW + MINUTE, percent: 9, resetsAt: null }] }, NOW), [])
  assert.equal(paceLines(buildStatus({ usage: null, now: NOW, bridge: null, samples: {}, latest: [] }), words12), NO_LIMITS)
})
