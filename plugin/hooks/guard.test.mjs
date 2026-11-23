// node --test: the budget guard's decisions (guard.js): options, when the
// budget is low (threshold, lead time, the weekly rule), what happens to one
// spawn (each mode, a resumed run, a fork, nobody to ask, the remembered
// answer), the question and its answers, and the downshift toast.
import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  GUARD_DEFAULTS, GUARD_HEADER, MAX_LEAD_MINUTES, REMEMBER_MS, SKIP_REASON, TOAST_EVERY_MS, WEEKLY_GUARD_AT, applyChoice,
  choiceOf, decide, downshiftToast, guardQuestion, guardSettings, lowBudget, modelName, toastDue,
} from './guard.js'
import { buildStatus, clock } from './status.js'

const MINUTE = 60_000
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
const SETTINGS = guardSettings({})

/** The status `/pace` speaks from, for these readings (and the widget's forecast, when given). */
function status({ five = 43, week = 14, hitsLimitAt = null, fiveReset = FIVE_RESET } = {}) {
  const rateLimits = []
  if (five !== null) rateLimits.push({ kind: 'five_hour', percentUsed: five, resetsAt: new Date(fiveReset).toISOString() })
  if (week !== null) rateLimits.push({ kind: 'seven_day', percentUsed: week, resetsAt: new Date(WEEK_RESET).toISOString() })
  const bridge = hitsLimitAt === null ? null : {
    five_hour: { resetsAt: fiveReset, hitsLimitAt, projectedAtReset: 130, confidence: 'high' },
  }
  return buildStatus({ usage: { rateLimits }, now: NOW, bridge, samples: {} })
}

const SPAWN = Object.freeze({ tool_use_id: 'toolu_1', prompt: 'Read the tests.', description: 'Read tests',
  subagentType: 'general-purpose', parentModel: 'claude-opus-5-5', background: false, fork: false })

test('the options default to ask, 90%, 15 minutes and haiku', () => {
  assert.deepEqual(guardSettings(undefined), { mode: 'ask', threshold: 90, leadMinutes: 15, model: 'haiku' })
  assert.deepEqual(guardSettings({}), GUARD_DEFAULTS)
})

test('the options take what the manifest allows and fall back otherwise', () => {
  assert.deepEqual(guardSettings({ guard: 'downshift', guardThreshold: 75, guardLeadMinutes: 0, guardModel: ' sonnet ' }),
    { mode: 'downshift', threshold: 75, leadMinutes: 0, model: 'sonnet' })
  assert.equal(guardSettings({ guard: 'off' }).mode, 'off')
  assert.equal(guardSettings({ guard: 'deny' }).mode, 'ask')
  assert.equal(guardSettings({ guardThreshold: '85' }).threshold, 85)
  for (const bad of [0, 101, -5, NaN, 'lots', '', null, true]) {
    assert.equal(guardSettings({ guardThreshold: bad }).threshold, 90, `threshold ${bad}`)
  }
  assert.equal(guardSettings({ guardThreshold: 1 }).threshold, 1)
  assert.equal(guardSettings({ guardThreshold: 100 }).threshold, 100)
  assert.equal(guardSettings({ guardLeadMinutes: MAX_LEAD_MINUTES }).leadMinutes, MAX_LEAD_MINUTES)
  for (const bad of [-1, MAX_LEAD_MINUTES + 1, 'soon', Infinity]) {
    assert.equal(guardSettings({ guardLeadMinutes: bad }).leadMinutes, 15, `lead ${bad}`)
  }
  assert.equal(guardSettings({ guardModel: 'claude-haiku-5-5' }).model, 'claude-haiku-5-5')
  assert.equal(guardSettings({ guardModel: 'claude-opus-5-5[1m]' }).model, 'claude-opus-5-5[1m]')
  for (const bad of ['', '   ', 'haiku; rm -rf /', 'two words', 42, '-haiku', 'x'.repeat(101)]) {
    assert.equal(guardSettings({ guardModel: bad }).model, 'haiku', `model ${JSON.stringify(bad)}`)
  }
})

test('the 5-hour window is low from the threshold on', () => {
  assert.equal(lowBudget(status({ five: 89.9 }), SETTINGS, NOW), null)
  const low = lowBudget(status({ five: 90 }), SETTINGS, NOW)
  assert.deepEqual(low, { kind: 'five_hour', reason: 'threshold', percentUsed: 90, resetsAt: FIVE_RESET, hitsLimitAt: null,
    limitReached: false })
  assert.equal(lowBudget(status({ five: 60 }), { ...SETTINGS, threshold: 60 }, NOW)?.reason, 'threshold')
  assert.equal(lowBudget(status({ five: 104 }), SETTINGS, NOW)?.limitReached, true)
  // A used-up window is low even with the threshold at 100 and above it.
  assert.equal(lowBudget(status({ five: 100 }), { ...SETTINGS, threshold: 100 }, NOW)?.reason, 'threshold')
})

test('the 5-hour window is low when the pace says the limit comes within the lead time', () => {
  const at = (minutes) => lowBudget(status({ five: 70, hitsLimitAt: NOW + minutes * MINUTE }), SETTINGS, NOW)
  assert.deepEqual(at(15), { kind: 'five_hour', reason: 'forecast', percentUsed: 70, resetsAt: FIVE_RESET,
    hitsLimitAt: NOW + 15 * MINUTE, limitReached: false })
  assert.equal(at(1)?.reason, 'forecast')
  assert.equal(at(15.02), null)
  assert.equal(at(90), null)
  // Lead time 0: only a limit that is due now.
  assert.equal(lowBudget(status({ five: 70, hitsLimitAt: NOW + MINUTE }), { ...SETTINGS, leadMinutes: 0 }, NOW), null)
  // No forecast (too few readings, or the limit comes after the reset): not low.
  assert.equal(lowBudget(status({ five: 70 }), SETTINGS, NOW), null)
})

test('the weekly window is low from 97%, whatever the threshold', () => {
  assert.equal(lowBudget(status({ five: 10, week: 96.9 }), SETTINGS, NOW), null)
  assert.equal(WEEKLY_GUARD_AT, 97)
  assert.deepEqual(lowBudget(status({ five: 10, week: 97 }), SETTINGS, NOW), { kind: 'seven_day', reason: 'weekly',
    percentUsed: 97, resetsAt: WEEK_RESET, hitsLimitAt: null, limitReached: false })
  // The threshold is the 5-hour window's only.
  assert.equal(lowBudget(status({ five: 10, week: 95 }), { ...SETTINGS, threshold: 50 }, NOW), null)
  assert.equal(lowBudget(status({ five: null, week: 99 }), SETTINGS, NOW)?.kind, 'seven_day')
})

test('the most used low window speaks, the 5-hour one on a tie', () => {
  assert.equal(lowBudget(status({ five: 92, week: 98 }), SETTINGS, NOW)?.kind, 'seven_day')
  assert.equal(lowBudget(status({ five: 99, week: 98 }), SETTINGS, NOW)?.kind, 'five_hour')
  assert.equal(lowBudget(status({ five: 98, week: 98 }), SETTINGS, NOW)?.kind, 'five_hour')
  // A forecast at 70% beside a week at 97%: the week.
  assert.equal(lowBudget(status({ five: 70, week: 97, hitsLimitAt: NOW + 5 * MINUTE }), SETTINGS, NOW)?.kind, 'seven_day')
})

test('nothing is low without limits, after a reset, or for other windows', () => {
  assert.equal(lowBudget(status({ five: null, week: null }), SETTINGS, NOW), null)
  assert.equal(lowBudget(buildStatus({ usage: null, now: NOW, bridge: null, samples: {} }), SETTINGS, NOW), null)
  assert.equal(lowBudget(undefined, SETTINGS, NOW), null)
  // The reset passed: the window started over.
  assert.equal(lowBudget(status({ five: 99, fiveReset: NOW - MINUTE }), SETTINGS, NOW), null)
  const spend = buildStatus({ usage: { rateLimits: [{ kind: 'spend_limit', percentUsed: 99 },
    { kind: 'seven_day_opus', percentUsed: 99 }] }, now: NOW, bridge: null, samples: {} })
  assert.equal(lowBudget(spend, SETTINGS, NOW), null)
})

test('each mode decides what happens to a spawn near the limit', () => {
  const low = lowBudget(status({ five: 92 }), SETTINGS, NOW)
  const base = { low, spawn: SPAWN, canAsk: true, memory: null, now: NOW }
  assert.deepEqual(decide({ ...base, settings: SETTINGS }), { action: 'ask' })
  assert.deepEqual(decide({ ...base, settings: { ...SETTINGS, mode: 'downshift' } }), { action: 'apply', choice: 'downshift' })
  assert.deepEqual(decide({ ...base, settings: { ...SETTINGS, mode: 'off' } }), { action: 'pass' })
})

test('the guard leaves a spawn alone when the budget is fine, a run resumes, or no one can be asked', () => {
  const low = lowBudget(status({ five: 92 }), SETTINGS, NOW)
  for (const mode of ['ask', 'downshift']) {
    const settings = { ...SETTINGS, mode }
    const base = { settings, low, spawn: SPAWN, canAsk: true, memory: null, now: NOW }
    assert.deepEqual(decide({ ...base, low: null }), { action: 'pass' }, `${mode}: fine`)
    assert.deepEqual(decide({ ...base, spawn: { ...SPAWN, resume: { agentId: 'a1' } } }), { action: 'pass' }, `${mode}: resume`)
    assert.deepEqual(decide({ ...base, canAsk: false }), { action: 'pass' }, `${mode}: headless`)
  }
})

test('downshift leaves a fork alone: it always runs on the parent model', () => {
  const low = lowBudget(status({ five: 92 }), SETTINGS, NOW)
  const settings = { ...SETTINGS, mode: 'downshift' }
  assert.deepEqual(decide({ settings, low, spawn: { ...SPAWN, fork: true }, canAsk: true, memory: null, now: NOW }),
    { action: 'pass' })
  // In ask mode a fork is still asked (run or skip).
  assert.deepEqual(decide({ settings: SETTINGS, low, spawn: { ...SPAWN, fork: true }, canAsk: true, memory: null, now: NOW }),
    { action: 'ask' })
})

test('an answer holds for ten minutes', () => {
  const low = lowBudget(status({ five: 92 }), SETTINGS, NOW)
  const at = (memoryAt, choice = 'skip') => decide({ settings: SETTINGS, low, spawn: SPAWN, canAsk: true,
    memory: { choice, at: memoryAt }, now: NOW })
  assert.deepEqual(at(NOW), { action: 'apply', choice: 'skip' })
  assert.deepEqual(at(NOW - REMEMBER_MS + 1, 'downshift'), { action: 'apply', choice: 'downshift' })
  assert.deepEqual(at(NOW - 3 * MINUTE, 'run'), { action: 'apply', choice: 'run' })
  assert.deepEqual(at(NOW - REMEMBER_MS), { action: 'ask' })
  // A clock set back: the answer is from "the future" and asked again.
  assert.deepEqual(at(NOW + MINUTE), { action: 'ask' })
  // Remembered or not, a fine budget is left alone.
  assert.deepEqual(decide({ settings: SETTINGS, low: null, spawn: SPAWN, canAsk: true, memory: { choice: 'skip', at: NOW },
    now: NOW }), { action: 'pass' })
})

test('the question says how much is left and when it resets, Run anyway first', () => {
  const q = guardQuestion({ low: lowBudget(status({ five: 92 }), SETTINGS, NOW), words: words12, model: 'haiku',
    isFork: false })
  assert.equal(q.question, 'About 8% of the 5-hour window is left (resets 4:30 PM). ' +
    'Starting subagents now will probably use it up. Start the subagents?')
  assert.equal(q.header, GUARD_HEADER)
  assert.ok(q.header.length <= 12)
  assert.deepEqual(q.options, ['Run anyway', 'Use Haiku for subagents', 'Skip subagents'])
  assert.deepEqual(q.labels, { run: 'Run anyway', downshift: 'Use Haiku for subagents', skip: 'Skip subagents' })
  assert.ok(q.question.endsWith('?'))
})

test('the question for a used-up window, a forecast, the week, a 24-hour clock and a fork', () => {
  const ask = (input, words = words12, isFork = false, model = 'haiku') =>
    guardQuestion({ low: lowBudget(status(input), SETTINGS, NOW), words, model, isFork })
  assert.equal(ask({ five: 100 }).question, 'The 5-hour window is used up (resets 4:30 PM). Start the subagents?')
  assert.equal(ask({ five: 99.6 }).question.startsWith('Less than 1% of the 5-hour window is left'), true)
  assert.equal(ask({ five: 70, hitsLimitAt: Date.parse('2026-11-17T13:12:00Z') }).question,
    'At this pace the 5-hour window runs out about 1:12 PM, before it resets at 4:30 PM. ' +
    'Starting subagents now will probably use it up. Start the subagents?')
  assert.equal(ask({ five: 10, week: 98 }).question, 'About 2% of the weekly window is left (resets Fri 3:30 PM). ' +
    'Starting subagents now will probably use it up. Start the subagents?')
  assert.equal(ask({ five: 92 }, words24).question.includes('(resets 16:30)'), true)
  const fork = ask({ five: 92 }, words12, true)
  assert.deepEqual(fork.options, ['Run anyway', 'Skip subagents'])
  assert.equal(fork.labels.downshift, undefined)
  assert.deepEqual(ask({ five: 92 }, words12, false, 'claude-sonnet-5-5').options[1], 'Use claude-sonnet-5-5 for subagents')
})

test('only an exact label is a choice; anything else runs the subagent', () => {
  const { labels } = guardQuestion({ low: lowBudget(status({ five: 92 }), SETTINGS, NOW), words: words12, model: 'haiku',
    isFork: false })
  assert.equal(choiceOf('Run anyway', labels), 'run')
  assert.equal(choiceOf('Use Haiku for subagents', labels), 'downshift')
  assert.equal(choiceOf('Skip subagents', labels), 'skip')
  for (const other of ['skip subagents', 'Skip', 'please wait', '', undefined, 'Run anyway, Skip subagents']) {
    assert.equal(choiceOf(other, labels), 'run', JSON.stringify(other))
  }
  // A fork's question has no smaller model to choose.
  assert.equal(choiceOf('Use Haiku for subagents', { run: 'Run anyway', skip: 'Skip subagents' }), 'run')
})

test('a choice runs the spawn as it was, on the guard model, or refuses it with the reason', () => {
  assert.deepEqual(applyChoice(SPAWN, 'run', 'haiku'), { type: 'next', input: SPAWN })
  assert.deepEqual(applyChoice(SPAWN, 'downshift', 'haiku'), { type: 'next', input: { ...SPAWN, model: 'haiku' } })
  assert.deepEqual(applyChoice({ ...SPAWN, model: 'opus' }, 'downshift', 'sonnet').input.model, 'sonnet')
  assert.deepEqual(applyChoice(SPAWN, 'skip', 'haiku'), { type: 'deny', reason: SKIP_REASON })
  assert.equal(SKIP_REASON, 'The user chose to save usage budget; continue without parallel subagents.')
  // A fork ignores a model: it runs as it was.
  const fork = { ...SPAWN, fork: true }
  assert.deepEqual(applyChoice(fork, 'downshift', 'haiku'), { type: 'next', input: fork })
  // The input is never changed in place (it is frozen).
  assert.equal(SPAWN.model, undefined)
})

test('downshift says so once per ten minutes', () => {
  assert.equal(toastDue(null, NOW), true)
  assert.equal(toastDue(NOW - TOAST_EVERY_MS + 1, NOW), false)
  assert.equal(toastDue(NOW - TOAST_EVERY_MS, NOW), true)
  assert.equal(toastDue(NOW + MINUTE, NOW), true)
  assert.equal(downshiftToast(lowBudget(status({ five: 92 }), SETTINGS, NOW), 'haiku'),
    'Budget guard: subagents run on Haiku while the 5-hour window is at 92%')
  assert.equal(downshiftToast(lowBudget(status({ five: 100 }), SETTINGS, NOW), 'sonnet'),
    'Budget guard: subagents run on Sonnet while the 5-hour window is used up')
  assert.equal(downshiftToast(lowBudget(status({ five: 10, week: 97.4 }), SETTINGS, NOW), 'haiku'),
    'Budget guard: subagents run on Haiku while the weekly window is at 97%')
  assert.equal(downshiftToast(lowBudget(status({ five: 70, hitsLimitAt: NOW + 5 * MINUTE }), SETTINGS, NOW), 'haiku'),
    'Budget guard: subagents run on Haiku while the 5-hour window runs out soon at this pace')
})

test('model names: an alias is capitalized, an id stays', () => {
  assert.equal(modelName('haiku'), 'Haiku')
  assert.equal(modelName('sonnet'), 'Sonnet')
  assert.equal(modelName('claude-haiku-5-5'), 'claude-haiku-5-5')
})
