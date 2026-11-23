// The pace arithmetic of Sticky AI Usage in JavaScript: where a usage window
// is heading at the current rate. It follows the same definition as the
// widget's C# PaceCalculator, and both are pinned by the shared vectors in
// tests/vectors/pace (pace.test.mjs runs every one). Pure: times are
// milliseconds since the epoch, `now` is passed in, nothing touches `$`.
//
// 1. Only the samples of the current window count (same reset within two minutes).
// 2. The rate is measured over a lookback by kind: 5-hour 30 minutes, weekly
//    24 hours, a spend cap 7 days, anything else a tenth of its length.
// 3. Rate = least-squares slope of the samples inside the lookback, never
//    below zero; it needs 3 samples spanning a third of the lookback and a
//    rise of 1 point (2 for whole-point sources), else it is unknown.
// 4. Active = the last rise is recent (10 minutes, 6 hours, 2 days).
// 5. The limit time is only given while active, and only before the reset;
//    never in the past. It runs from the newest sample, whose percentage is
//    as of its own time.
// 6. The projection at the reset uses the recent rate while active and the
//    window's average so far otherwise.
// 7. Even pace = the share of the window that has passed.
// 8. Confidence: high with 6 samples spanning 80% of the lookback and R² ≥ 0.8.

export const MINUTE = 60_000
export const HOUR = 60 * MINUTE
export const DAY = 24 * HOUR

/** Two readings of one window may report its reset a little apart. */
export const SAME_RESET_MS = 2 * MINUTE

const WEEKLY = new Set(['seven_day', 'seven_day_opus', 'seven_day_sonnet'])
const SPEND = new Set(['month_spend', 'spend_limit'])

/** What the rate is measured over, by kind (ms). */
export function lookbackMs(kind, lengthMs) {
  if (kind === 'five_hour') return 30 * MINUTE
  if (WEEKLY.has(kind)) return DAY
  if (SPEND.has(kind)) return 7 * DAY
  return lengthMs > 0 ? lengthMs / 10 : 30 * MINUTE
}

/** How recent the last rise must be for the window to count as in use (ms). */
export function activeForMs(kind, lookback) {
  if (kind === 'five_hour') return 10 * MINUTE
  if (WEEKLY.has(kind)) return 6 * HOUR
  if (SPEND.has(kind)) return 2 * DAY
  return lookback / 3
}

/** A window's length when the kind says it (ms), else null. */
export function lengthOfKind(kind) {
  if (kind === 'five_hour') return 5 * HOUR
  if (WEEKLY.has(kind)) return 7 * DAY
  return null
}

/** Whether two resets (ms or null) are the same window's. */
export function sameReset(a, b) {
  if (a == null) return b == null
  return b != null && Math.abs(a - b) <= SAME_RESET_MS
}

function describe(ms) {
  const days = ms / DAY
  const hours = ms / HOUR
  if (days >= 1 && days % 1 === 0) return days === 1 ? '24 hours' : `${days} days`
  if (hours >= 1 && hours % 1 === 0) return hours === 1 ? 'hour' : `${hours} hours`
  return `${Math.round(ms / MINUTE)} minutes`
}

/** Slope in points per hour and R² of a straight line through the samples. */
function leastSquares(points) {
  const origin = points[0].at
  const n = points.length
  let sx = 0
  let sy = 0
  for (const p of points) {
    sx += (p.at - origin) / HOUR
    sy += p.percent
  }
  const mx = sx / n
  const my = sy / n
  let sxx = 0
  let sxy = 0
  let syy = 0
  for (const p of points) {
    const dx = (p.at - origin) / HOUR - mx
    const dy = p.percent - my
    sxx += dx * dx
    sxy += dx * dy
    syy += dy * dy
  }
  if (sxx <= 0) return { slope: 0, rSquared: 0 }
  return { slope: sxy / sxx, rSquared: syy <= 0 ? 0 : (sxy * sxy) / (sxx * syy) }
}

/** When the percentage last went up, or null if it never did. */
function lastRise(ordered) {
  for (let i = ordered.length - 1; i > 0; i--) {
    if (ordered[i].percent > ordered[i - 1].percent) return ordered[i].at
  }
  return null
}

/**
 * The forecast of one window.
 * @param {object} input
 * @param {string} input.kind  `five_hour`, `seven_day`, `spend_limit`, …
 * @param {{at: number, percent: number, resetsAt: number|null}[]} input.samples
 * @param {number} input.now
 * @param {number|null} input.resetsAt  the window's reset
 * @param {number|null} [input.lengthMs]  the window's length
 * @param {boolean} [input.wholePointsOnly]
 * @returns {{kind: string, percentNow: number, ratePerHour: number|null, hitsLimitAt: number|null,
 *   projectedAtReset: number|null, aheadOfEvenPace: number|null, confidence: 'none'|'low'|'high',
 *   basis: string, isActive: boolean, resetsAt: number|null}}
 */
export function pace({ kind, samples, now, resetsAt, lengthMs = null, wholePointsOnly = false }) {
  const lookback = lookbackMs(kind, lengthMs)
  const basis = `the last ${describe(lookback)}`
  const none = (percentNow) => ({
    kind, percentNow, ratePerHour: null, hitsLimitAt: null, projectedAtReset: null, aheadOfEvenPace: null,
    confidence: 'none', basis, isActive: false, resetsAt: resetsAt ?? null,
  })

  const current = (samples ?? [])
    .filter((s) => s && Number.isFinite(s.at) && Number.isFinite(s.percent) && s.at <= now &&
      sameReset(s.resetsAt ?? null, resetsAt ?? null))
    .sort((a, b) => a.at - b.at)
  // A window whose reset has passed has started over: 0%, nothing to forecast.
  if (resetsAt != null && resetsAt <= now) return none(0)
  if (current.length === 0) return none(0)

  const newest = current[current.length - 1]
  const percentNow = newest.percent

  const recent = current.filter((s) => s.at >= now - lookback)
  let rate = null
  let rSquared = 0
  if (recent.length >= 3 &&
      recent[recent.length - 1].at - recent[0].at >= lookback / 3 &&
      recent[recent.length - 1].percent - recent[0].percent >= (wholePointsOnly ? 2 : 1)) {
    const fit = leastSquares(recent)
    rate = Math.max(0, fit.slope)
    rSquared = fit.rSquared
  }

  const rise = lastRise(current)
  const isActive = rise !== null && rise >= now - activeForMs(kind, lookback)

  let hitsLimitAt = null
  if (isActive && rate > 0 && percentNow < 100 && resetsAt != null) {
    let at = newest.at + ((100 - percentNow) / rate) * HOUR
    if (at < now) at = now
    if (at < resetsAt) hitsLimitAt = at
  }

  let projectedAtReset = null
  let aheadOfEvenPace = null
  if (resetsAt != null) {
    if (isActive && rate !== null) projectedAtReset = percentNow + (rate * (resetsAt - newest.at)) / HOUR
    if (lengthMs > 0) {
      const elapsed = Math.min(lengthMs, Math.max(0, lengthMs - (resetsAt - now)))
      aheadOfEvenPace = percentNow - (100 * elapsed) / lengthMs
      if (projectedAtReset === null && elapsed > 0) {
        projectedAtReset = percentNow + (percentNow / (elapsed / HOUR)) * ((resetsAt - now) / HOUR)
      }
    }
  }

  const confidence = rate === null ? 'none'
    : recent.length >= 6 && recent[recent.length - 1].at - recent[0].at >= lookback * 0.8 && rSquared >= 0.8
      ? 'high'
      : 'low'

  return {
    kind, percentNow, ratePerHour: rate, hitsLimitAt, projectedAtReset, aheadOfEvenPace, confidence, basis,
    isActive, resetsAt: resetsAt ?? null,
  }
}
