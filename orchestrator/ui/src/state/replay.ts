import { signal } from '@preact/signals';
import { postSimulate } from '../api/client';
import type { DetectorName, SimulateResponse } from '../api/types';

/**
 * Replay state, keyed BY ENTITY (E1).
 *
 * A module-level `replayState` signal would be shared by every mount of the panel, and the
 * detector editor is a per-entity route: navigating `#/detectors/sensor/A` -> `.../B` reuses
 * the same component instance, so the chart for A would sit under B's heading until the new
 * result arrived. On a slow sensor that is minutes of a confidently wrong picture. Keying the
 * map by entityId, plus the reset effect the panel runs on [entityId], makes that state
 * unreachable rather than merely unlikely.
 */
export type ReplayState =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'done'; result: SimulateResponse }
  | { kind: 'error'; message: string };

const IDLE: ReplayState = { kind: 'idle' };

/** Matches the panel's debounce; also the window the in-flight gate protects. */
export const DEBOUNCE_MS = 400;

export const replayStates = signal<Record<string, ReplayState>>({});
export const replayEnabled = signal<Record<string, boolean>>({});

/**
 * One in-flight request per entity — the Gate.Wait(0) shape: a run that arrives while
 * another is outstanding is DROPPED, not queued. Queueing would let a slider drag build a
 * backlog of replays that each cost a real detector pass, and the last one to finish (not
 * the last one requested) would win the render.
 */
const inFlight = new Set<string>();
const timers = new Map<string, ReturnType<typeof setTimeout>>();

export function replayFor(entityId: string): ReplayState {
  return replayStates.value[entityId] ?? IDLE;
}

export function isReplayEnabled(entityId: string): boolean {
  return replayEnabled.value[entityId] === true;
}

export function setReplayEnabled(entityId: string, on: boolean): void {
  replayEnabled.value = { ...replayEnabled.value, [entityId]: on };
}

/** Clears this entity's result, pending debounce and enabled flag. Idempotent. */
export function resetReplay(entityId: string): void {
  const timer = timers.get(entityId);
  if (timer !== undefined) {
    clearTimeout(timer);
    timers.delete(entityId);
  }
  inFlight.delete(entityId);

  const nextStates = { ...replayStates.value };
  delete nextStates[entityId];
  replayStates.value = nextStates;

  const nextEnabled = { ...replayEnabled.value };
  delete nextEnabled[entityId];
  replayEnabled.value = nextEnabled;
}

export interface ReplayParams {
  detector: DetectorName;
  params: Record<string, string>;
  lookback: string;
  maxPoints: number;
}

function setState(entityId: string, state: ReplayState): void {
  replayStates.value = { ...replayStates.value, [entityId]: state };
}

/** Runs one replay immediately, subject to the per-entity in-flight gate. */
export async function runReplay(entityId: string, params: ReplayParams): Promise<void> {
  if (inFlight.has(entityId)) return;
  inFlight.add(entityId);
  setState(entityId, { kind: 'running' });

  try {
    const result = await postSimulate(entityId, {
      detector: params.detector,
      params: params.params,
      lookback: params.lookback,
      maxPoints: params.maxPoints,
    });
    setState(entityId, { kind: 'done', result });
  } catch (err) {
    // A transport failure is reported, never swallowed into an empty chart: "did not run"
    // and "ran and found nothing" are opposite answers to the operator's question.
    setState(entityId, {
      kind: 'error',
      message: err instanceof Error ? err.message : String(err),
    });
  } finally {
    inFlight.delete(entityId);
  }
}

/**
 * Debounced entry point used by the panel on every parameter edit. Restarting the timer on
 * each keystroke means a burst of edits costs one replay, and blocker §7 #13 (each history
 * fetch is a fresh WebSocket connect + auth to HA Core) is why that is a correctness concern
 * and not a nicety.
 */
export function scheduleReplay(entityId: string, params: ReplayParams): void {
  const existing = timers.get(entityId);
  if (existing !== undefined) clearTimeout(existing);

  timers.set(
    entityId,
    setTimeout(() => {
      timers.delete(entityId);
      void runReplay(entityId, params);
    }, DEBOUNCE_MS),
  );
}
