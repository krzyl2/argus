import { useEffect, useRef } from 'preact/hooks';
import type { DetectorName, ReplayEpisodeSpan, SimulateResponse } from '../api/types';
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
 * Episode bands: the x-ranges the SERVER's gate held ON, translated to chart coordinates.
 *
 * The panel derives nothing here. It used to re-run a hysteresis state machine over `scores`
 * against an absolute high/low — a third implementation of a gate that only `alert_mode: legacy`
 * entities ever run, so on the default adaptive path the picture and the numbers came from two
 * different systems. Both disagreements were reachable and neither was visible: a raw-channel
 * episode (robust z on the value, which never reaches the browser) printed "1 epizod" over an
 * unshaded chart, and the uncalibrated opening stretch of every replay printed "0 epizodów"
 * over a shaded band. summary.episodeSpans is the same array summary.episodes was counted
 * from, so the two cannot drift.
 */
function bandsOf(spans: ReplayEpisodeSpan[], count: number): Array<{ x: number; width: number }> {
  if (count < 2) return [];
  const step = SVG_WIDTH / (count - 1);

  return spans.map((span) => {
    // endIndex is exclusive and reaches `count` for an episode still open at the end of the
    // history; the last drawable x is count - 1.
    const end = Math.min(span.endIndex, count - 1);
    return {
      x: span.startIndex * step,
      width: Math.max(1, (end - span.startIndex) * step),
    };
  });
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
        <ReplayResult result={state.result} />
      )}
    </section>
  );
}

interface ReplayResultProps {
  result: SimulateResponse;
}

function ReplayResult({ result }: ReplayResultProps) {
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

  const bands = bandsOf(summary.episodeSpans ?? [], values.length);

  // The adaptive gate starts every replay COLD: the rank channel needs alert_min_samples
  // verdicts before it may fire, so the opening stretch was decided by the raw channel alone.
  // That is faithful to a restarted add-on, but it also means the episode count depends on how
  // much lookback the operator asked for — a dependency the panel has to state, not hide.
  const step = values.length > 1 ? SVG_WIDTH / (values.length - 1) : 0;
  const calibratedFrom = Math.min(summary.calibratedFromIndex ?? 0, values.length - 1);
  const blindPoints = Math.max(0, calibratedFrom - result.warmedUpFromIndex);

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

      {blindPoints > 0 && (
        <p class="argus-label">
          Pierwsze {blindPoints} pkt bez kanału wyniku — polityka startuje zimna (jak po
          restarcie), więc do kalibracji rangi decyduje wyłącznie kanał surowy. Liczby powyżej
          obejmują ten odcinek.
        </p>
      )}

      {/* Inline SVG, no chart library (precedent: AttributionBar). */}
      <svg
        class="argus-replay-panel__chart"
        viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
        preserveAspectRatio="none"
        role="img"
        aria-label="Odtworzenie historii: wartość, wynik i pasy epizodów"
      >
        {blindPoints > 0 && (
          <rect
            class="argus-replay-panel__uncalibrated"
            x={result.warmedUpFromIndex * step}
            y={0}
            width={Math.max(1, blindPoints * step)}
            height={SVG_HEIGHT}
          />
        )}
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
