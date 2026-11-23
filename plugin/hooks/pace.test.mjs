// node --test: the plugin's pace() against the shared vectors the widget's C#
// tests also run (tests/vectors/pace), plus the properties both promise.
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'

import { DAY, HOUR, MINUTE, SAME_RESET_MS, lengthOfKind, lookbackMs, pace, sameReset } from './pace.js'

const VECTORS = fileURLToPath(new URL('../../tests/vectors/pace/', import.meta.url))
const files = readdirSync(VECTORS).filter((f) => f.endsWith('.json')).sort()

test('there are at least twelve shared vectors', () => {
  assert.ok(files.length >= 12, `found ${files.length}`)
})

/** A vector's input in pace()'s terms (times relative to `now`, in minutes). */
function inputOf(vector) {
  const now = Date.parse(vector.now)
  const resetsAt = now + vector.resetsInMinutes * MINUTE
  return {
    kind: vector.kind,
    now,
    resetsAt,
    lengthMs: vector.lengthMinutes * MINUTE,
    wholePointsOnly: vector.wholePointsOnly,
    samples: vector.samples.map((s) => ({
      at: now + s.minutes * MINUTE,
      percent: s.percent,
      resetsAt: s.resetsInMinutes === undefined ? resetsAt : now + s.resetsInMinutes * MINUTE,
    })),
  }
}

function near(actual, expected, tolerance, what) {
  assert.equal(actual !== null, expected !== null, `${what}: ${actual} vs ${expected}`)
  if (expected !== null) {
    assert.ok(Math.abs(actual - expected) <= tolerance, `${what}: ${actual} not within ${tolerance} of ${expected}`)
  }
}

for (const file of files) {
  const vector = JSON.parse(readFileSync(VECTORS + file, 'utf8'))
  test(`vector ${file}: ${vector.name}`, () => {
    assert.equal(vector.schema, 1)
    const input = inputOf(vector)
    const forecast = pace(input)
    const { expected, tolerance } = vector

    assert.ok(Math.abs(forecast.percentNow - expected.percentNow) < 0.1, 'percentNow')
    assert.equal(forecast.ratePerHour !== null, expected.rateKnown, 'rateKnown')
    if (typeof expected.ratePerHour === 'number') {
      const relative = tolerance.ratePerHourRelative
      assert.ok(forecast.ratePerHour >= expected.ratePerHour * (1 - relative) &&
        forecast.ratePerHour <= expected.ratePerHour * (1 + relative),
      `ratePerHour ${forecast.ratePerHour} vs ${expected.ratePerHour}`)
    }
    assert.equal(forecast.confidence, expected.confidence, 'confidence')

    const hitsIn = typeof expected.hitsLimitInMinutes === 'number' ? expected.hitsLimitInMinutes : null
    near(forecast.hitsLimitAt === null ? null : (forecast.hitsLimitAt - input.now) / MINUTE, hitsIn,
      tolerance.hitsLimitMinutes, 'hitsLimitInMinutes')
    if ('projectedAtReset' in expected) {
      near(forecast.projectedAtReset, expected.projectedAtReset, tolerance.percent, 'projectedAtReset')
    }
    near(forecast.aheadOfEvenPace, expected.aheadOfEvenPace ?? null, tolerance.percent, 'aheadOfEvenPace')
  })
}

const NOW = Date.parse('2026-11-17T14:00:00Z')

/** count samples, `perStep` points apart, every `step` minutes, ending now. */
function steady(resetsAt, start, perStep, count, step = 5) {
  return Array.from({ length: count }, (_, i) => ({
    at: NOW - step * (count - 1 - i) * MINUTE, percent: start + perStep * i, resetsAt,
  }))
}

test('thirty minutes of steady burn gives the limit time within ten minutes', () => {
  // 2 points a minute from 30%: 100% at 35 minutes from the first reading, 5 from now.
  const resetsAt = NOW + 2 * HOUR
  const samples = steady(resetsAt, 30, 2, 31, 1)
  const forecast = pace({ kind: 'five_hour', samples, now: NOW, resetsAt, lengthMs: 5 * HOUR })
  assert.equal(forecast.confidence, 'high')
  assert.ok(Math.abs(forecast.hitsLimitAt - (NOW + 5 * MINUTE)) <= 10 * MINUTE)
})

test('samples from the future and of another window are ignored', () => {
  const resetsAt = NOW + 2 * HOUR
  const samples = [...steady(resetsAt, 40, 5, 7), { at: NOW + 10 * MINUTE, percent: 99, resetsAt },
    { at: NOW - MINUTE, percent: 3, resetsAt: resetsAt + 5 * HOUR }]
  const forecast = pace({ kind: 'five_hour', samples, now: NOW, resetsAt, lengthMs: 5 * HOUR })
  assert.equal(forecast.percentNow, 70)
})

test('nothing to forecast without samples, or once the reset has passed', () => {
  const none = pace({ kind: 'five_hour', samples: [], now: NOW, resetsAt: NOW + HOUR, lengthMs: 5 * HOUR })
  assert.equal(none.confidence, 'none')
  assert.equal(none.hitsLimitAt, null)
  const past = pace({ kind: 'five_hour', samples: steady(NOW - MINUTE, 40, 5, 7), now: NOW, resetsAt: NOW - MINUTE })
  assert.equal(past.percentNow, 0)
  assert.equal(past.confidence, 'none')
})

test('broken samples are skipped, not thrown on', () => {
  const resetsAt = NOW + 2 * HOUR
  const samples = [null, { at: 'x', percent: 1 }, { at: NOW, percent: Number.NaN }, ...steady(resetsAt, 40, 5, 7)]
  assert.equal(pace({ kind: 'five_hour', samples, now: NOW, resetsAt, lengthMs: 5 * HOUR }).percentNow, 70)
  assert.equal(pace({ kind: 'five_hour', samples: undefined, now: NOW, resetsAt }).confidence, 'none')
})

test('the limit time is never in the past and never after the reset (random traces)', () => {
  // A fixed seed: the same 2,000 traces every run.
  let seed = 7
  const random = () => ((seed = (seed * 1103515245 + 12345) % 2 ** 31) / 2 ** 31)
  for (let trace = 0; trace < 2000; trace++) {
    const resetsAt = NOW + Math.floor(random() * 300) * MINUTE
    let percent = random() * 90
    let at = NOW - Math.floor(random() * 60) * MINUTE
    const samples = []
    while (at <= NOW) {
      samples.push({ at, percent, resetsAt })
      percent += random() < 0.3 ? 0 : random() * 6
      at += Math.max(1, Math.floor(random() * 8)) * MINUTE
    }
    const forecast = pace({ kind: 'five_hour', samples, now: NOW, resetsAt, lengthMs: 5 * HOUR })
    if (forecast.hitsLimitAt !== null) {
      assert.ok(forecast.hitsLimitAt >= NOW, `trace ${trace}: in the past`)
      assert.ok(forecast.hitsLimitAt < resetsAt, `trace ${trace}: after the reset`)
    }
    if (forecast.ratePerHour !== null) assert.ok(forecast.ratePerHour >= 0)
  }
})

test('no forecast without a rise', () => {
  const resetsAt = NOW + 2 * HOUR
  const flat = steady(resetsAt, 40, 0, 10)
  const forecast = pace({ kind: 'five_hour', samples: flat, now: NOW, resetsAt, lengthMs: 5 * HOUR })
  assert.equal(forecast.ratePerHour, null)
  assert.equal(forecast.hitsLimitAt, null)
})

test('lookbacks, lengths and the same-reset rule follow the widget', () => {
  assert.equal(lookbackMs('five_hour'), 30 * MINUTE)
  assert.equal(lookbackMs('seven_day'), DAY)
  assert.equal(lookbackMs('seven_day_opus'), DAY)
  assert.equal(lookbackMs('spend_limit'), 7 * DAY)
  assert.equal(lookbackMs('custom', 10 * HOUR), HOUR)
  assert.equal(lookbackMs('custom', null), 30 * MINUTE)
  assert.equal(lengthOfKind('five_hour'), 5 * HOUR)
  assert.equal(lengthOfKind('seven_day'), 7 * DAY)
  assert.equal(lengthOfKind('spend_limit'), null)
  assert.ok(sameReset(NOW, NOW + SAME_RESET_MS))
  assert.ok(!sameReset(NOW, NOW + SAME_RESET_MS + 1))
  assert.ok(sameReset(null, undefined))
  assert.ok(!sameReset(null, NOW))
})
