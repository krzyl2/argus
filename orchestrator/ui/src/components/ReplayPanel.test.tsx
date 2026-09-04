import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, cleanup, fireEvent } from '@testing-library/preact';
import { ReplayPanel } from './ReplayPanel';
import { replayStates, replayEnabled, DEBOUNCE_MS } from '../state/replay';
import * as client from '../api/client';
import type { SimulateResponse } from '../api/types';

function response(overrides: Partial<SimulateResponse> = {}): SimulateResponse {
  return {
    ok: true,
    error: null,
    summary: {
      episodes: 2,
      onTimePercent: 3.7,
      spanHours: 24,
      alertsPerDay: 2,
      scorablePoints: 120,
      transitions: 4,
      episodeSpans: [
        { startIndex: 101, endIndex: 120 },
        { startIndex: 140, endIndex: 160 },
      ],
      calibratedFromIndex: 60,
    },
    scores: Array.from({ length: 180 }, (_, i) => (i > 100 ? 0.9 : 0.1)),
    values: Array.from({ length: 180 }, (_, i) => 100 + (i % 5)),
    timestamps: Array.from({ length: 180 }, (_, i) =>
      new Date(Date.UTC(2026, 8, 3, 0, i)).toISOString(),
    ),
    warmedUpFromIndex: 60,
    window: 60,
    ...overrides,
  };
}

beforeEach(() => {
  replayStates.value = {};
  replayEnabled.value = {};
  vi.restoreAllMocks();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('ReplayPanel', () => {
  // E1. The state map is module-level and the detector editor is a per-entity route, so the
  // same component instance is reused when the operator moves from one sensor to the next.
  // Without the reset, sensor A's chart and A's episode counts sit under B's heading until
  // B's own run finishes — minutes of a confidently wrong picture on a slow sensor.
  it('resets result when entityId changes', async () => {
    replayStates.value = { 'sensor.a': { kind: 'done', result: response() } };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);
    expect(view.container.textContent).toMatch(/epizody/);

    view.rerender(<ReplayPanel entityId="sensor.b" detector="rmad" params={{}} />);

    expect(view.container.textContent).not.toMatch(/epizody/);
    expect(replayStates.value['sensor.a']).toBeUndefined();
  });

  // Debounce 400 ms + Gate.Wait(0). Every run is a real detector pass over up to 5000 points
  // and, on an influx_url-less install, a fresh WebSocket connect + auth to HA Core
  // (docs/FIX-PLAN.md §7 #13). A slider drag that fired one request per keystroke would be a
  // connection storm against Core, and the last response to arrive — not the last one
  // requested — would win the render.
  it('single in-flight request under rapid param edits', async () => {
    vi.useFakeTimers();
    let resolveCall: ((value: SimulateResponse) => void) | null = null;
    const post = vi.spyOn(client, 'postSimulate').mockImplementation(
      () => new Promise<SimulateResponse>((resolve) => { resolveCall = resolve; }),
    );

    const view = render(
      <ReplayPanel entityId="sensor.a" detector="rmad" params={{ high_threshold: '0.5' }} />,
    );

    // Ask for the first run, then edit the parameter five times inside the debounce window.
    fireEvent.click(view.getByText('Uruchom odtworzenie'));
    for (const value of ['0.51', '0.52', '0.53', '0.54', '0.55']) {
      view.rerender(
        <ReplayPanel entityId="sensor.a" detector="rmad" params={{ high_threshold: value }} />,
      );
      vi.advanceTimersByTime(100);
    }

    expect(post).toHaveBeenCalledTimes(0);

    vi.advanceTimersByTime(DEBOUNCE_MS);
    expect(post).toHaveBeenCalledTimes(1);

    // While that one is outstanding, further edits are DROPPED, not queued.
    view.rerender(
      <ReplayPanel entityId="sensor.a" detector="rmad" params={{ high_threshold: '0.6' }} />,
    );
    vi.advanceTimersByTime(DEBOUNCE_MS * 3);
    expect(post).toHaveBeenCalledTimes(1);

    resolveCall!(response());
  });

  // A/F4. hst scores rarity, not deviation, and its own normalizer collapses the normal band
  // after one excursion. A chart drawn from those scores with no label on it would read as an
  // endorsement of the numbers printed next to it.
  it('renders legacy badge for hst', () => {
    const withHst = render(<ReplayPanel entityId="sensor.a" detector="hst" params={{}} />);
    expect(withHst.container.textContent).toMatch(/legacy/);
    expect(withHst.container.textContent).toMatch(/F4/);

    cleanup();

    const withRmad = render(<ReplayPanel entityId="sensor.b" detector="rmad" params={{}} />);
    expect(withRmad.container.textContent).not.toMatch(/legacy/);
  });

  // Opening an editor must not cost a detector pass and an HA connection. The panel is a
  // deliberate action, not a page-load side effect.
  it('does not run on mount', () => {
    const post = vi.spyOn(client, 'postSimulate').mockResolvedValue(response());

    render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(post).toHaveBeenCalledTimes(0);
  });

  // "Did not run" and "ran and found nothing" are opposite answers. A zero-episode chart in
  // place of an error message is exactly the kind of confidently-wrong number this whole fix
  // exists to remove.
  it('shows the error instead of an empty chart when the run fails', () => {
    replayStates.value = {
      'sensor.a': {
        kind: 'done',
        result: response({ ok: false, error: 'Unimplemented', summary: null }),
      },
    };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.textContent).toMatch(/Unimplemented/);
    expect(view.container.textContent).not.toMatch(/epizody/);
  });

  // The window number in this message is unwritable without SimulateResponse.window — which
  // is why the field is on the wire at all.
  it('says how much history is missing when nothing is scorable', () => {
    replayStates.value = {
      'sensor.a': {
        kind: 'done',
        result: response({
          scores: [0, 0, 0],
          values: [1, 2, 3],
          timestamps: [],
          warmedUpFromIndex: 3,
          summary: {
            episodes: 0,
            onTimePercent: 0,
            spanHours: 0,
            alertsPerDay: 0,
            scorablePoints: 0,
            transitions: 0,
            episodeSpans: [],
            calibratedFromIndex: 3,
          },
        }),
      },
    };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.textContent).toMatch(/3\/60 pkt/);
  });

  // The chart and the header must be ONE statement. The panel used to shade its bands by
  // re-running a hysteresis state machine over `scores` against an absolute high/low — a gate
  // only `alert_mode: legacy` entities run — while the header came from the server's real
  // decision path. On the default adaptive path the two disagreed in both directions, and the
  // operator has no way to tell which half is lying.
  it('shades exactly the episodes the header counts when the raw channel fired', () => {
    // The reproducible case: a score flat at 0.0 (nothing near any threshold) while the raw
    // value steps 100 -> 500. The live gate fires on robust z, which never reaches the browser,
    // so a client-side band derivation shades nothing under a header saying "1".
    replayStates.value = {
      'sensor.a': {
        kind: 'done',
        result: response({
          scores: Array.from({ length: 180 }, () => 0),
          values: Array.from({ length: 180 }, (_, i) => (i >= 100 && i < 110 ? 500 : 100)),
          summary: {
            episodes: 1,
            onTimePercent: 5,
            spanHours: 3,
            alertsPerDay: 8,
            scorablePoints: 120,
            transitions: 2,
            episodeSpans: [{ startIndex: 100, endIndex: 110 }],
            calibratedFromIndex: 60,
          },
        }),
      },
    };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.querySelectorAll('.argus-replay-panel__band')).toHaveLength(1);
  });

  it('shades nothing when the gate never fired, however high the scores are', () => {
    // The mirror image, and the opening stretch of EVERY adaptive replay: scores pegged at 0.9
    // while the rank channel is still uncalibrated, so the live gate stays silent. A band here
    // tells the operator the sensor was in alarm during a stretch it would have spent quiet.
    replayStates.value = {
      'sensor.a': {
        kind: 'done',
        result: response({
          scores: Array.from({ length: 180 }, () => 0.9),
          summary: {
            episodes: 0,
            onTimePercent: 0,
            spanHours: 3,
            alertsPerDay: 0,
            scorablePoints: 120,
            transitions: 0,
            episodeSpans: [],
            calibratedFromIndex: 60,
          },
        }),
      },
    };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.textContent).toMatch(/epizody/);
    expect(view.container.querySelectorAll('.argus-replay-panel__band')).toHaveLength(0);
  });

  // The adaptive replay starts the policy cold, exactly like a restarted add-on, so the rank
  // channel needs alert_min_samples verdicts before it may fire at all. The numbers above the
  // chart cover that stretch, which means they depend on how much lookback was asked for — a
  // dependency an operator tuning thresholds has to be able to see.
  it('marks the stretch that had no score channel', () => {
    replayStates.value = {
      'sensor.a': {
        kind: 'done',
        result: response({ summary: { ...response().summary!, calibratedFromIndex: 120 } }),
      },
    };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.textContent).toMatch(/bez kanału wyniku/);
    expect(
      view.container.querySelectorAll('.argus-replay-panel__uncalibrated'),
    ).toHaveLength(1);
  });

  it('says nothing about calibration when the score channel was live throughout', () => {
    // An invented caveat is as bad as a missing one: on the legacy path there is no
    // calibration phase, and a permanent warning would train the operator to ignore it.
    replayStates.value = { 'sensor.a': { kind: 'done', result: response() } };

    const view = render(<ReplayPanel entityId="sensor.a" detector="rmad" params={{}} />);

    expect(view.container.textContent).not.toMatch(/bez kanału wyniku/);
    expect(
      view.container.querySelectorAll('.argus-replay-panel__uncalibrated'),
    ).toHaveLength(0);
  });
});
