// Sticky AI Usage: Claude Code's own usage figures, for you, for Claude and
// for the desktop widget.
//
// What it does, all of it:
// - reads `$.session.usage()` (the figures Claude Code's status line has:
//   plan-limit windows, context fill, session cost) and a few session facts
//   (id, folder, model, version, surface);
// - feed: writes them as JSON to the widget's feed folder after each turn, on
//   session start and end, and on a one-minute heartbeat when something
//   changed (option `feed`);
// - pace: keeps the plan-limit readings in its own store (`$.store`, the last
//   200 per window, shared by your sessions) to work out when a limit will be
//   reached at the current pace; reads the widget's `forecast.json` when the
//   widget is running, so both say the same time;
// - adds `/pace`, a tool Claude can call (`usage_status`, option
//   `exposeTool`), a one-line band above the prompt (option `band`) and, near
//   a limit, one short note beside your prompt for Claude (options
//   `contextNote`, `contextNoteThreshold`). It never changes what you typed.
// - budget guard (option `guard`): near a limit, before subagents start, one
//   question (run anyway, run them on a smaller model, or skip them), or in
//   `downshift` the smaller model at once. Nothing where no one can be asked.
// - session lights (with the feed): the record says whether the session is
//   working, waiting for you (a permission, a question, an MCP server's
//   input) or done, from Claude Code's turn, tool and notification events.
//   Only the kind of wait and a tool's name: never a prompt, command or path.
// - nothing else. No network, no commands, no model calls, no reading of
//   your sign-in. `claude plugin validate` lists every call it makes.

import {
  HEARTBEAT_MS, buildRecord, contentKey, continuesAfterEnd, feedFolder, inFolder, markerRecord, sessionFileName,
  shouldWrite,
} from './feed.js'
import {
  ORDER, addSample, bandLine, bandShows, buildStatus, clock, contextNote, latestLimits, paceJson, paceLines,
  prefersHour12, readBridge, samplesKey, samplesOf, toMs,
} from './status.js'
import { PERMISSION_CONFIRM_MS, initialState, nextState, notificationEvent, recordFields } from './state.js'
import {
  applyChoice, choiceOf, decide, downshiftToast, guardQuestion, guardSettings, lowBudget, toastDue,
} from './guard.js'

const TOOL = 'usage_status'
const TOOL_ID = 'mcp__sticky-usage__usage_status'
const TOOL_DESCRIPTION = "Returns the user's current Claude plan usage: percent used and reset time for each limit " +
  'window, the estimated time the limit will be reached at the current pace, context-window use and session cost. ' +
  'Call it before starting long or parallel work, or when the user asks about limits, quota or budget.'

// Per load of the module (a hot reload starts over, which is harmless: the
// next write simply happens, and the note's count starts again).
let folder
let lastKey = null
let lastWrittenAt = 0
let lastSessionId = null
let rateLimitsAt = null
let activity = initialState(null)
let claudeCodeVersion = null
let hasLoggedFailure = false
let hasLoggedSurfaceFailure = false
let noteMemory = { prompts: 0, notedAt: null, notedPercent: null }
// The budget guard: the last answer (ten minutes), the question on screen (a
// fan-out's spawns all wait for it), and when `downshift` last said so.
let guardMemory = null
let guardAsking = null
let guardToastAt = null
const hour12 = prefersHour12()

/** Works the feed folder out once per load. */
async function resolveFolder($) {
  if (folder !== undefined) return folder
  const home = (await $.env.get('HOME')) ?? (await $.env.get('USERPROFILE'))
  const isMac = home ? await $.fs.exists(`${home}/Library/Application Support`) : false
  folder = feedFolder({
    override: await $.env.get('STICKYAIUSAGE_FEED_DIR'),
    localAppData: await $.env.get('LOCALAPPDATA'),
    home,
    xdgDataHome: await $.env.get('XDG_DATA_HOME'),
    isMac,
  })
  return folder
}

/** Says once per session that the feed failed, and why; never throws. */
function logFailure($, error) {
  if (hasLoggedFailure) return
  hasLoggedFailure = true
  try {
    $.ui.log(`sticky-usage: could not write the usage feed (${String(error)}). The widget falls back to its other sources.`)
  } catch {
    // Nothing more to do: the feed is best effort.
  }
}

/** Says once per load that /pace, the tool, the band or the note failed; never throws. */
function logSurfaceFailure($, what, error) {
  if (hasLoggedSurfaceFailure) return
  hasLoggedSurfaceFailure = true
  try {
    $.ui.log(`sticky-usage: ${what} failed (${String(error)}).`)
  } catch {
    // Best effort, like the feed.
  }
}

/** `$.session.usage()`, or null on a build or host without it. */
async function readUsage($) {
  try {
    return await $.session.usage()
  } catch {
    return null
  }
}

/** Writes this session's record when it changed (or `force`). */
async function writeRecord($, force) {
  try {
    const target = await resolveFolder($)
    if (!target) throw new Error('no folder for the feed (LOCALAPPDATA and HOME are unset)')

    const sessionId = await $.session.id()
    if (sessionId !== lastSessionId) {
      // A /clear goes on under a new id: a new file, written at once.
      lastSessionId = sessionId
      lastKey = null
      hasLoggedFailure = false
    }

    // An older or different build without usage(): the record still says
    // the session is there, with "rateLimits": null.
    const usage = await readUsage($)

    const now = await $.clock.now()
    const surfaces = await $.session.surfaces()
    const record = buildRecord({
      usage,
      sessionId,
      cwd: await $.session.cwd(),
      model: await $.session.model(),
      surface: surfaces[0] ?? 'headless',
      claudeCodeVersion,
      now,
      rateLimitsAt,
      ...recordFields(activity),
    })
    const key = contentKey(record)
    if (!shouldWrite({ force, key, lastKey, now, lastWrittenAt })) return

    await $.fs.write(inFolder(target, sessionFileName(sessionId)), JSON.stringify(record))
    lastKey = key
    lastWrittenAt = now
  } catch (error) {
    logFailure($, error)
  }
}

/** Moves the session's state (state.js) and writes the record when it
 *  changed; never throws. A permission dialog also sets the timer that takes
 *  it as answered when Claude Code never says it is still open. */
async function track($, event) {
  try {
    const previous = activity
    activity = nextState(activity, { ...event, at: await $.clock.now() })
    if (activity === previous) return
    await writeRecord($, true)
    if (event.type === 'permission' && !activity.confirmed) {
      const episode = activity.since
      $.clock.after(PERMISSION_CONFIRM_MS, () => {
        track($, { type: 'permission.timeout', episode })
      })
    }
  } catch (error) {
    logFailure($, error)
  }
}

/** The main loop's events only: a subagent's turn or tool says nothing about
 *  whether the session waits for you (its permission asks still do). */
const isMainLoop = (e) => e.agentId === undefined && e.agent_id === undefined

/** After a tool ran, failed or was refused. */
async function toolDone($, e, next) {
  const result = await next(e)
  await track($, { type: 'tool.done', tool: e.tool_name })
  return result
}

/** Adds the readings Claude Code just measured to the shared samples. */
async function recordSamples($, rateLimits) {
  const now = await $.clock.now()
  for (const limit of rateLimits) {
    const key = samplesKey(limit?.kind)
    if (key === null) continue
    const stored = await $.store.get(key)
    const updated = addSample(stored, limit.kind, { at: now, percent: limit.percentUsed, resetsAt: toMs(limit.resetsAt) })
    if (updated !== stored) await $.store.set(key, updated)
  }
}

/** The widget's forecast when it is running, else null. */
async function readWidgetForecast($, now) {
  try {
    const target = await resolveFolder($)
    return target ? readBridge(await $.fs.read(inFolder(target, 'forecast.json')), now) : null
  } catch {
    // No widget (no file yet), or a file being replaced: the plugin's own pace.
    return null
  }
}

/** What /pace, the tool, the band and the note speak from, and the time words. */
async function currentStatus($) {
  const now = await $.clock.now()
  const usage = await readUsage($)
  const own = Array.isArray(usage?.rateLimits) ? usage.rateLimits : []
  // A session reports limits only after its first answer: until then, the newest readings kept.
  const kinds = own.length > 0 ? own.map((limit) => limit?.kind) : ORDER
  const samples = {}
  for (const kind of kinds) {
    const key = samplesKey(kind)
    if (key === null) continue
    try {
      samples[kind] = samplesOf(await $.store.get(key))
    } catch {
      samples[kind] = []
    }
  }
  const latest = own.length > 0 ? [] : latestLimits(samples, now)
  const status = buildStatus({ usage, now, bridge: await readWidgetForecast($, now), samples, latest })
  return { status, words: clock({ now, hour12 }) }
}

/** The VS Code extension draws no band: its status line says the same, by the same rule. */
async function showStatusLine($, bandMode) {
  const surfaces = await $.session.surfaces()
  if (!surfaces.includes('vscode')) return
  const { status, words } = await currentStatus($)
  $.ui.status(bandShows(status, bandMode) ? bandLine(status, words).text : undefined)
}

/** Asks the one question, or waits for the one already asked; resolves to a
 *  choice and remembers it. A dismissed dialog runs the subagent. */
function askOnce($, question) {
  if (guardAsking === null) {
    guardAsking = $.ui.ask(question.question, { options: question.options, header: question.header })
      .then((answer) => choiceOf(answer, question.labels), () => 'run')
      .then(async (choice) => {
        // Remembering is best effort; the answer stands either way.
        try {
          guardMemory = { choice, at: await $.clock.now() }
        } catch (error) {
          logSurfaceFailure($, 'the budget guard', error)
        }
        return choice
      })
      .finally(() => {
        guardAsking = null
      })
  }
  return guardAsking
}

/** The budget guard on a subagent that is about to start (guard.js decides). */
async function guardSpawn($, e, next, settings) {
  if (e.resume !== undefined) return next(e)
  // No surface, no one to ask: claude -p and the Agent SDK run as they are.
  const canAsk = (await $.session.surfaces()).length > 0
  const { status, words } = await currentStatus($)
  const now = await $.clock.now()
  const low = lowBudget(status, settings, now)
  const decided = decide({ settings, low, spawn: e, canAsk, memory: guardMemory, now })
  if (decided.action === 'pass') return next(e)

  let choice = decided.choice
  if (decided.action === 'ask') {
    choice = await askOnce($, guardQuestion({ low, words, model: settings.model, isFork: e.fork === true }))
  } else if (settings.mode === 'downshift' && toastDue(guardToastAt, now)) {
    guardToastAt = now
    try {
      $.ui.toast(downshiftToast(low, settings.model))
    } catch (error) {
      logSurfaceFailure($, 'the budget guard', error)
    }
  }
  const outcome = applyChoice(e, choice, settings.model)
  return outcome.type === 'deny' ? { deny: outcome.reason } : next(outcome.input)
}

/** A choice of the manifest's, or its default when unset or unknown. */
function choice(value, allowed, fallback) {
  return allowed.includes(value) ? value : fallback
}

/** The note's threshold, 1–100 (default 80). */
function threshold(value) {
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) && number >= 1 && number <= 100 ? number : 80
}

/** @type {import('claude-code').Register} */
export const register = (on, options) => {
  const feedOn = options?.feed !== false
  const bandMode = choice(options?.band, ['auto', 'always', 'never'], 'auto')
  const noteOn = options?.contextNote !== false
  const noteAt = threshold(options?.contextNoteThreshold)
  const toolOn = options?.exposeTool !== false
  const guard = guardSettings(options)

  on('session.start', async ($, e, next) => {
    // A taken name throws: the rest of the plugin goes on without it.
    try {
      await $.command.register({
        name: 'pace',
        description: 'Plan limits, reset times and the pace forecast (Sticky AI Usage)',
        argumentHint: '[json]',
        immediate: true,
      })
    } catch (error) {
      logSurfaceFailure($, '/pace', error)
    }
    if (toolOn) {
      try {
        await $.tool.register({
          name: TOOL,
          description: TOOL_DESCRIPTION,
          inputSchema: { type: 'object', properties: {}, additionalProperties: false },
          isDeferred: false,
        })
      } catch (error) {
        logSurfaceFailure($, 'the usage_status tool', error)
      }
    }
    const result = await next(e)
    if (feedOn) {
      try {
        claudeCodeVersion = (await $.session.version()).version
        activity = initialState(await $.clock.now())
        const target = await resolveFolder($)
        if (target) {
          const marker = markerRecord({ claudeCodeVersion, surface: e.surface ?? 'headless', now: await $.clock.now() })
          await $.fs.write(inFolder(target, 'plugin.json'), JSON.stringify(marker))
        }
        await writeRecord($, true)
        $.clock.every(HEARTBEAT_MS, () => writeRecord($, false))
      } catch (error) {
        logFailure($, error)
      }
    }
    return result
  })

  on('command.run', { command: 'pace' }, async ($, e) => {
    try {
      const { status, words } = await currentStatus($)
      const asJson = e.args.trim().toLowerCase() === 'json'
      return { text: asJson ? JSON.stringify(paceJson(status, words), null, 2) : paceLines(status, words) }
    } catch (error) {
      return { text: `Could not read the usage (${String(error)}).` }
    }
  })

  if (toolOn) {
    on('tool.call', { tool: TOOL_ID }, async ($) => {
      try {
        const { status, words } = await currentStatus($)
        return { result: JSON.stringify(paceJson(status, words)) }
      } catch (error) {
        return { result: JSON.stringify({ error: `Could not read the usage (${String(error)}).` }) }
      }
    }).catch(($, e, next) => (next.called ? next(e) : { result: JSON.stringify({ error: 'Could not read the usage.' }) }))
  }

  // After each main-thread turn, and when a limit moves a whole point: the
  // figures are Claude Code's reading of its latest response.
  on('session.measure', async ($, e, next) => {
    const result = await next(e)
    try {
      if (e.rateLimits.length > 0) {
        rateLimitsAt = await $.clock.now()
        await recordSamples($, e.rateLimits)
      }
    } catch (error) {
      logSurfaceFailure($, 'keeping the pace samples', error)
    }
    if (feedOn) await writeRecord($, false)
    if (bandMode !== 'never') {
      try {
        $.ui.invalidate('ui.render')
        await showStatusLine($, bandMode)
      } catch (error) {
        logSurfaceFailure($, 'the band', error)
      }
    }
    return result
  })

  if (noteOn) {
    on('prompt.submit', async ($, e, next) => {
      // Only what the person typed; a slash command's line takes no context.
      if (e.command !== undefined || e.isMeta === true) return next(e)
      let note = null
      try {
        const { status, words } = await currentStatus($)
        const decided = contextNote({ status, threshold: noteAt, memory: noteMemory, words })
        noteMemory = decided.memory
        note = decided.note
      } catch (error) {
        logSurfaceFailure($, 'the context note', error)
      }
      return next(note ? { ...e, context: [...(e.context ?? []), note] } : e)
    }).catch(($, e, next) => next(e))
  }

  if (bandMode !== 'never') {
    // A failure draws what Claude Code would have drawn: the band never breaks the prompt.
    on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
      if (e.props?.hasSurvey) return next(e)
      let band = null
      try {
        const { status, words } = await currentStatus($)
        if (bandShows(status, bandMode)) {
          const line = bandLine(status, words)
          const { Text } = $.ui.resolve(e)
          const props = line.level ? { color: line.level, wrap: 'truncate-end' } : { dimColor: true, wrap: 'truncate-end' }
          band = h(Text, props, line.text)
        }
      } catch (error) {
        logSurfaceFailure($, 'the band', error)
      }
      return band ?? next(e)
    })
  }

  // Near a limit, one question before subagents start (or a smaller model, in
  // `downshift`). Off registers nothing. A failure lets the subagent start.
  if (guard.mode !== 'off') {
    on('agent.spawn', ($, e, next) => guardSpawn($, e, next, guard)).catch(($, e, next) => next(e))
  }

  if (!feedOn) return

  on('turn.start', async ($, e, next) => {
    const result = await next(e)
    if (isMainLoop(e)) await track($, { type: 'turn.start' })
    return result
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    if (isMainLoop(e)) {
      try {
        // A turn whose requests counted had a response, and with it the limits.
        if (e.usage !== undefined) rateLimitsAt = await $.clock.now()
      } catch (error) {
        logFailure($, error)
      }
      await track($, {
        type: 'turn.complete', aborted: e.reason === 'aborted', error: e.reason === 'error', durationMs: e.durationMs,
      })
    }
    return result
  })

  // A permission dialog opens (any loop's: the person answers a subagent's
  // too). Marked while the hooks beneath run, so the light is amber at once;
  // a settings hook that answers it leaves no dialog to wait for.
  on('classic.PermissionRequest', async ($, e, next) => {
    const pending = next(e)
    await track($, { type: 'permission', tool: e.tool_name })
    const result = await pending
    if (result?.decision !== undefined) await track($, { type: 'tool.done', tool: e.tool_name })
    return result
  }).catch(($, e, next) => next(e))

  // The asking tool ran, failed or was refused: the dialog was answered.
  on('classic.PostToolUse', toolDone).catch(($, e, next) => next(e))
  on('classic.PostToolUseFailure', toolDone).catch(($, e, next) => next(e))
  on('classic.PermissionDenied', toolDone).catch(($, e, next) => next(e))

  on('classic.Notification', async ($, e, next) => {
    const result = await next(e)
    const event = notificationEvent(e.notification_type)
    if (event !== null) await track($, event)
    return result
  }).catch(($, e, next) => next(e))

  // An MCP server asks the person for input, and gets the answer.
  on('classic.Elicitation', async ($, e, next) => {
    const pending = next(e)
    await track($, { type: 'input' })
    return pending
  }).catch(($, e, next) => next(e))

  on('classic.ElicitationResult', async ($, e, next) => {
    const result = await next(e)
    await track($, { type: 'input.done' })
    return result
  }).catch(($, e, next) => next(e))

  // Claude asks the person a question: waiting until the dialog is answered.
  on('tool.call', { tool: 'AskUserQuestion' }, async ($, e, next) => {
    await track($, { type: 'question' })
    try {
      return await next(e)
    } finally {
      await track($, { type: 'question.answered' })
    }
  }).catch(($, e, next) => next(e))

  on('classic.StopFailure', async ($, e, next) => {
    const result = await next(e)
    if (e.error === 'rate_limit' && e.agent_id === undefined) await track($, { type: 'rate_limit' })
    return result
  }).catch(($, e, next) => next(e))

  on('session.end', async ($, e, next) => {
    await track($, { type: 'session.end' })
    if (continuesAfterEnd(e.reason)) {
      try {
        activity = initialState(await $.clock.now())
      } catch (error) {
        logFailure($, error)
      }
    }
    return next(e)
  })
}
