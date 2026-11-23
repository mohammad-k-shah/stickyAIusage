// The budget guard's decisions: pure, so node --test covers every rule
// (guard.test.mjs) and register.js only wires them to `agent.spawn`.
//
// Near a limit, a subagent that is about to start gets one question (or, in
// `downshift`, a smaller model): run anyway, run on the guard's model, or skip.
// The answer holds for ten minutes, so a fan-out of six asks once. Where no one
// can be asked (claude -p, the Agent SDK) the guard does nothing; it never
// denies without the person's answer.

import { labelsOf } from './status.js'

const MINUTE = 60_000

export const GUARD_MODES = ['ask', 'downshift', 'off']

/** The manifest's defaults (`guard`, `guardThreshold`, `guardLeadMinutes`, `guardModel`). */
export const GUARD_DEFAULTS = Object.freeze({ mode: 'ask', threshold: 90, leadMinutes: 15, model: 'haiku' })

/** The weekly window counts as low from this percent, whatever the threshold. */
export const WEEKLY_GUARD_AT = 97

/** How long an answer holds, and how often `downshift` says what it does. */
export const REMEMBER_MS = 10 * MINUTE
export const TOAST_EVERY_MS = 10 * MINUTE

/** The longest lead time the option takes (ten hours: a 5-hour window and more). */
export const MAX_LEAD_MINUTES = 600

export const GUARD_HEADER = 'Budget'

/** What Claude reads when the person skips the subagents. */
export const SKIP_REASON = 'The user chose to save usage budget; continue without parallel subagents.'

/** A model alias or id as the Agent tool takes it: `haiku`, `sonnet`, `claude-haiku-5-5`, `claude-opus-5-5[1m]`. */
const MODEL_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:[\]-]{0,99}$/

const numberOf = (value) => (typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : NaN)

/**
 * The guard's options from the plugin's `options`, each falling back to its
 * default when unset, out of range or of the wrong kind.
 * @returns {{mode: string, threshold: number, leadMinutes: number, model: string}}
 */
export function guardSettings(options) {
  const mode = GUARD_MODES.includes(options?.guard) ? options.guard : GUARD_DEFAULTS.mode
  const threshold = numberOf(options?.guardThreshold)
  const lead = numberOf(options?.guardLeadMinutes)
  const model = typeof options?.guardModel === 'string' ? options.guardModel.trim() : ''
  return {
    mode,
    threshold: Number.isFinite(threshold) && threshold >= 1 && threshold <= 100 ? threshold : GUARD_DEFAULTS.threshold,
    leadMinutes: Number.isFinite(lead) && lead >= 0 && lead <= MAX_LEAD_MINUTES ? lead : GUARD_DEFAULTS.leadMinutes,
    model: MODEL_PATTERN.test(model) ? model : GUARD_DEFAULTS.model,
  }
}

/**
 * Whether the budget is low, and by which window: the 5-hour window at or
 * above the threshold (or used up), the 5-hour forecast saying the limit comes
 * within the lead time, or the weekly window at or above 97%. The most used
 * window speaks when several are low (the 5-hour one on a tie).
 * @param {object} status what `buildStatus` returns
 * @returns {null | {kind: string, reason: 'threshold'|'forecast'|'weekly', percentUsed: number,
 *   resetsAt: number|null, hitsLimitAt: number|null, limitReached: boolean}}
 */
export function lowBudget(status, settings, now) {
  const low = []
  for (const window of status?.windows ?? []) {
    if (window.hasReset) continue
    let reason = null
    if (window.kind === 'five_hour') {
      if (window.limitReached || window.percentUsed >= settings.threshold) reason = 'threshold'
      else if (window.hitsLimitAt !== null && window.hitsLimitAt - now <= settings.leadMinutes * MINUTE) reason = 'forecast'
    } else if (window.kind === 'seven_day') {
      if (window.limitReached || window.percentUsed >= WEEKLY_GUARD_AT) reason = 'weekly'
    }
    if (reason === null) continue
    low.push({
      kind: window.kind,
      reason,
      percentUsed: window.percentUsed,
      resetsAt: window.resetsAt,
      hitsLimitAt: window.hitsLimitAt,
      limitReached: window.limitReached,
    })
  }
  if (low.length === 0) return null
  low.sort((a, b) => b.percentUsed - a.percentUsed || (a.kind === 'five_hour' ? -1 : b.kind === 'five_hour' ? 1 : 0))
  return low[0]
}

/**
 * What the guard does for one spawn, before any question:
 * `pass` (leave it alone), `ask`, or `apply` a choice (`run`, `downshift`, `skip`).
 * @param {object} input
 * @param {{mode: string}} input.settings
 * @param {object|null} input.low what `lowBudget` returned
 * @param {object} input.spawn the `agent.spawn` input (`resume`, `fork`)
 * @param {boolean} input.canAsk whether a surface draws (someone can answer)
 * @param {{choice: string, at: number}|null} input.memory the last answer
 * @param {number} input.now
 */
export function decide({ settings, low, spawn, canAsk, memory, now }) {
  if (settings.mode === 'off' || low === null || spawn?.resume !== undefined || !canAsk) return { action: 'pass' }
  if (settings.mode === 'downshift') return spawn?.fork === true ? { action: 'pass' } : { action: 'apply', choice: 'downshift' }
  if (memory && now >= memory.at && now - memory.at < REMEMBER_MS) return { action: 'apply', choice: memory.choice }
  return { action: 'ask' }
}

/** "Haiku" for the alias `haiku`; a full id stays as written. */
export function modelName(model) {
  return /^[a-z]+$/.test(model) ? model[0].toUpperCase() + model.slice(1) : model
}

/** "About 8%", "Less than 1%". */
function leftWords(percentUsed) {
  const left = Math.max(0, 100 - percentUsed)
  return left < 1 ? 'Less than 1%' : `About ${Math.round(left)}%`
}

/** "resets 4:30 PM" today, "resets Fri 3:30 PM" on another day. */
const resetWords = (low, words) => (low.resetsAt === null ? '' : ` (resets ${words.dayTime(low.resetsAt)})`)

/**
 * The one question: its text (ending in a question mark), its chip and its
 * choices, "Run anyway" first. A fork keeps the parent's model, so it is
 * offered no smaller one.
 * @returns {{question: string, header: string, options: string[], labels: {run: string, downshift?: string, skip: string}}}
 */
export function guardQuestion({ low, words, model, isFork }) {
  const [, , noun] = labelsOf(low.kind)
  let said
  if (low.limitReached) {
    said = `The ${noun} is used up${resetWords(low, words)}.`
  } else if (low.reason === 'forecast') {
    const reset = low.resetsAt === null ? '' : `, before it resets at ${words.dayTime(low.resetsAt)}`
    said = `At this pace the ${noun} runs out about ${words.dayTime(low.hitsLimitAt)}${reset}. ` +
      'Starting subagents now will probably use it up.'
  } else {
    said = `${leftWords(low.percentUsed)} of the ${noun} is left${resetWords(low, words)}. ` +
      'Starting subagents now will probably use it up.'
  }
  const labels = { run: 'Run anyway', skip: 'Skip subagents' }
  if (!isFork) labels.downshift = `Use ${modelName(model)} for subagents`
  const options = isFork ? [labels.run, labels.skip] : [labels.run, labels.downshift, labels.skip]
  return { question: `${said} Start the subagents?`, header: GUARD_HEADER, options, labels }
}

/**
 * The choice an answer names. Only an exact label counts: text typed under
 * "Other", an empty answer or anything else runs the subagent (the guard
 * never refuses without the person choosing to skip).
 */
export function choiceOf(answer, labels) {
  if (answer === labels.skip) return 'skip'
  if (labels.downshift !== undefined && answer === labels.downshift) return 'downshift'
  return 'run'
}

/**
 * What the hook does with a choice: `next` with the spawn as it was or on the
 * guard's model, or `deny` with the reason Claude reads.
 * @returns {{type: 'next', input: object} | {type: 'deny', reason: string}}
 */
export function applyChoice(spawn, choice, model) {
  if (choice === 'skip') return { type: 'deny', reason: SKIP_REASON }
  if (choice === 'downshift' && spawn.fork !== true) return { type: 'next', input: { ...spawn, model } }
  return { type: 'next', input: spawn }
}

/** Whether `downshift` says what it does now (once per ten minutes). */
export function toastDue(lastToastAt, now) {
  return lastToastAt === null || now < lastToastAt || now - lastToastAt >= TOAST_EVERY_MS
}

/** "Budget guard: subagents run on Haiku while the 5-hour window is at 92%". */
export function downshiftToast(low, model) {
  const [, , noun] = labelsOf(low.kind)
  const state = low.limitReached ? 'is used up' : `is at ${Math.round(low.percentUsed)}%`
  const why = low.reason === 'forecast' ? `the ${noun} runs out soon at this pace` : `the ${noun} ${state}`
  return `Budget guard: subagents run on ${modelName(model)} while ${why}`
}
