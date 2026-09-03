import { useEffect, useRef } from 'preact/hooks';
import type { DetectorName, SimulateResponse } from '../api/types';
import {
  isReplayEnabled,
  replayFor,
  replayStates,
  replayEnabled,
  resetReplay,
  scheduleReplay,
  setReplayEnabled,
} from '../state/replay';
import { Badge } from './Badge';
import { Button } from './Button';

interface ReplayPanelProps {
  entityId: string;
  detector: DetectorName;
  params: Record<string, string>;
}

/** B5 — the window every acceptance number in the fix plan is stated in. */
const LOOKBACK = '24h';
const MAX_POINTS = 2000;

const SVG_WIDTH = 640;
const SVG_HEIGHT = 120;

function fmt(value: number, digits = 1): string {
  return value.toFixed(digits).replace('.', ',');
}

function scaleY(value: number, min: number, max: number): number {
  if (!(max > min)) return SVG_HEIGHT / 2;
  return SVG_HEIGHT - ((value - min) / (max - min)) * SVG_HEIGHT;
}

function polyline(values: number[], from: number, min: number, max: number): string {
  const count = values.length;
  if (count < 2) return '';
  const step = SVG_WIDTH / (count - 1);
  const points: string[] = [];
  for (let i = from; i < count; i++) {
    points.push(`${(i * step).toFixed(1)},${scaleY(values[i], min, max).toFixed(1)}`);
  }
  return points.join(' ');
}

/**
 * Episode bands: the x-ranges where the gate would have been ON, derived from the same
 * high/low thresholds and min_consecutive the server used. Recomputed here only for drawing —
 * the NUMBERS come from the server's ReplaySimulator so the panel can never disagree with the
 * acceptance measurement.
 */
function episodeBands(
  result: SimulateResponse,
  high: number,
  low: number,
  minConsecutive: number,
): Array<{ x: number; width: number }> {
  const count = result.scores.length;
  if (count < 2) return [];
  const step = SVG_WIDTH / (count - 1);

  const bands: Array<{ x: number; width: number }> = [];
  let on = false;
  let runHigh = 0;
  let runLow = 0;
  let startIndex = 0;

  for (let i = result.warmedUpFromIndex; i < count; i++) {
    const score = result.scores[i];
    if (score > high) {
      runHigh++;
      runLow = 0;
      if (!on && runHigh >= minConsecutive) {
        on = true;
        startIndex = i;
      }
    } else if (score < low) {
      runLow++;
      runHigh = 0;
      if (on && runLow >= minConsecutive) {
        on = false;
        bands.push({ x: startIndex * step, width: Math.max(1, (i - startIndex) * step) });
      }
    } else {
      runHigh = 0;
      runLow = 0;
    }
  }
  if (on) {
    bands.push({ x: startIndex * step, width: Math.max(1, (count - 1 - startIndex) * step) });
  }
  return bands;
}

/**
 * "Testuj na historii" — replays this sensor's own Recorder/InfluxDB history through the real
 * detector and the real gate, so the operator can see what a parameter change would have done
 * BEFORE saving it.
 *
 * Three deliberate behaviours:
 *  - It does NOT run on mount. Every run is a real detector pass over up to 5000 points and,
 *    on an influx_url-less install, a fresh WebSocket connect to HA Core; opening an editor
 *    must not cost that.
 *  - It resets on [entityId] (E1), so a result for A can never render under B's heading.
 *  - On `hst` it says so. The simulation is honest about what it replayed, and hst scores
 *    rarity rather than deviation (F4) — a chart with no such label would read as an
 *    endorsement of the numbers on it.
 */
export function ReplayPanel({ entityId, detector, params }: ReplayPanelProps) {
  // E1: the state map is module-level and the editor route reuses this component across
  // entities. Clearing on entityId CHANGE is what makes "A's chart under B's heading"
  // structurally impossible rather than merely unlikely.
  //
  // The previous-id ref is load-bearing: a bare [entityId] effect also fires on first mount,
  // which would throw away a result that is already on screen whenever a parent re-mounts
  // this panel (a sensor-list refresh does exactly that).
  const previousEntityId = useRef(entityId);
  useEffect(() => {
    if (previousEntityId.current !== entityId) {
      resetReplay(previousEntityId.current);
      resetReplay(entityId);
      previousEntityId.current = entityId;
    }
  }, [entityId]);

  // Subscribe to both signals so a state change re-renders (signals are read through
  // helpers, which would otherwise hide the dependency from the reactive system).
  replayStates.value;
  replayEnabled.value;

  const state = replayFor(entityId);
  const enabled = isReplayEnabled(entityId);

  const high = Number(params.high_threshold ?? '0.5') || 0.5;
  const low = Number(params.low_threshold ?? '0.375') || 0.375;
  const minConsecutive = Number(params.min_consecutive ?? '3') || 3;

  function run() {
    setReplayEnabled(entityId, true);
    scheduleReplay(entityId, { detector, params, lookback: LOOKBACK, maxPoints: MAX_POINTS });
  }

  // Re-run on every parameter edit, but ONLY after the operator has asked for a first run.
  useEffect(() => {
    if (!enabled) return;
    scheduleReplay(entityId, { detector, params, lookback: LOOKBACK, maxPoints: MAX_POINTS });
  }, [entityId, detector, JSON.stringify(params), enabled]);

  return (
    <section class="argus-replay-panel">
      <header class="argus-replay-panel__header">
        <h2 class="argus-replay-panel__title">Testuj na historii</h2>
        {detector === 'hst' && <Badge tone="warn">legacy — niekalibrowany (F4)</Badge>}
        <Button variant="ghost" size="sm" onClick={run}>
          {state.kind === 'running' ? 'Liczę…' : 'Uruchom odtworzenie'}
        </Button>
      </header>

      <p class="argus-label">
        Symulacja od zera, bez checkpointu — model startuje pusty, więc wynik nie musi być
        identyczny z modelem, który działa na żywo od tygodni.
      </p>

      {state.kind === 'error' && (
        <p class="argus-label argus-replay-panel__error">
          Nie udało się odtworzyć historii: {state.message}
        </p>
      )}

      {state.kind === 'done' && !state.result.ok && (
        <p class="argus-label argus-replay-panel__error">
          {state.result.error ?? 'Brak wyniku odtworzenia.'}
        </p>
      )}

      {state.kind === 'done' && state.result.ok && state.result.summary && (
        <ReplayResult
          result={state.result}
          high={high}
          low={low}
          minConsecutive={minConsecutive}
        />
      )}
    </section>
  );
}

interface ReplayResultProps {
  result: SimulateResponse;
  high: number;
  low: number;
  minConsecutive: number;
}

function ReplayResult({ result, high, low, minConsecutive }: ReplayResultProps) {
  const summary = result.summary!;

  if (summary.scorablePoints === 0) {
    return (
      <p class="argus-label">
        Za mało historii do rozgrzewki ({result.values.length}/{result.window} pkt)
      </p>
    );
  }

  const values = result.values;
  const scored = values.slice(result.warmedUpFromIndex);
  const min = Math.min(...scored);
  const max = Math.max(...scored);

  const bands = episodeBands(result, high, low, minConsecutive);

  return (
    <>
      <dl class="argus-replay-panel__numbers">
        <div>
          <dt class="argus-label">epizody</dt>
          <dd>{summary.episodes}</dd>
        </div>
        <div>
          <dt class="argus-label">alertów/dobę</dt>
          <dd>{fmt(summary.alertsPerDay)}</dd>
        </div>
        <div>
          <dt class="argus-label">on-time %</dt>
          <dd>{fmt(summary.onTimePercent)}</dd>
        </div>
        <div>
          <dt class="argus-label">zakres h</dt>
          <dd>{fmt(summary.spanHours)}</dd>
        </div>
      </dl>

      {/* Inline SVG, no chart library (precedent: AttributionBar). */}
      <svg
        class="argus-replay-panel__chart"
        viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
        preserveAspectRatio="none"
        role="img"
        aria-label="Odtworzenie historii: wartość, wynik i pasy epizodów"
      >
        {bands.map((band) => (
          <rect
            class="argus-replay-panel__band"
            x={band.x}
            y={0}
            width={band.width}
            height={SVG_HEIGHT}
          />
        ))}
        <polyline
          class="argus-replay-panel__value"
          fill="none"
          points={polyline(values, result.warmedUpFromIndex, min, max)}
        />
        <polyline
          class="argus-replay-panel__score"
          fill="none"
          points={polyline(result.scores, result.warmedUpFromIndex, 0, 1)}
        />
      </svg>
    </>
  );
}
