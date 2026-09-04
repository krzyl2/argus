import type { DetectorEntry, DetectorName } from '../api/types';
import { validateDetectorParams } from '../validation/detectorParams';
import { defaultsFor } from '../state/detectorDefaults';
import { FieldValidationError } from './FieldValidationError';
import { Input } from './Input';

/**
 * Everything a help line needs to say what a raw number MEANS for THIS sensor.
 *
 * Optional as a whole (see the prop default): SensorListRow renders this grid without any of
 * it, and a missing piece must degrade to "say less", never to a fabricated figure.
 */
export interface FieldCtx {
  /** Measured seconds between readings, or null when the pipeline has not measured it yet. */
  medianIntervalSec: number | null;
  /** rmad's score-squashing constant; the inverse of a threshold is z = zScale*t/(1-t). */
  zScale: number;
  /** The sensor's own unit, for scale_floor. Null when HA reports none. */
  unitOfMeasurement: string | null;
}

const DEFAULT_CTX: FieldCtx = { medianIntervalSec: null, zScale: 5, unitOfMeasurement: null };

interface DetectorParamGridProps {
  entityIdx: number;
  detIdx: number;
  detector: DetectorEntry;
  onParamChange: (key: string, value: string) => void;
  /** Optional so existing call sites (SensorListRow) compile unchanged. */
  ctx?: FieldCtx;
}

interface FieldSpec {
  key: string;
  label: string;
  step?: string;
  span2?: boolean;
  unit?: string;
  /** Renders under the field: what this number means for THIS sensor, in Polish (D8). */
  help?: (raw: string, ctx: FieldCtx) => string | null;
  /** Renders as a warning under the field when the value is defensible but probably wrong. */
  warn?: (raw: string, ctx: FieldCtx) => string | null;
}

/** z = z_scale * t / (1 - t) — the inverse of rmad's score squashing (D-B). */
export function robustZ(threshold: number, zScale: number): number {
  return (zScale * threshold) / (1 - threshold);
}

function pl(n: number, digits = 1): string {
  return n.toFixed(digits).replace('.', ',');
}

/** Formats a span of seconds the way an operator reads it: minutes, hours, or days. */
export function wallClockSpan(seconds: number): string {
  if (seconds < 90) return `${Math.round(seconds)} s`;
  const minutes = seconds / 60;
  if (minutes < 90) return `${pl(minutes, 0)} min`;
  // Hours all the way up, deliberately: F6-3 wants a 720-sample window on a 391 s/reading
  // sensor to read as ~78 h. "3,3 dni" rounds away the very number the >48 h warning is about.
  return `${pl(minutes / 60)} h`;
}

const HOURS_48 = 48 * 3600;

// Field set/order/defaults must match BuildHstParamGrid/BuildMadParamGrid/BuildStlParamGrid exactly.
const HST_FIELDS: FieldSpec[] = [
  { key: 'window', label: 'window' },
  { key: 'n_trees', label: 'n_trees' },
  { key: 'high_threshold', label: 'high_threshold', step: '0.01' },
  { key: 'low_threshold', label: 'low_threshold', step: '0.01' },
  { key: 'min_consecutive', label: 'min_consecutive' },
  { key: 'frozen_window', label: 'frozen_window' },
  { key: 'frozen_variance_threshold', label: 'frozen_variance', step: '0.0001', span2: true },
];

/**
 * rmad fields. The help text exists because the numbers are DIMENSIONLESS by design (D-B) —
 * that is what makes one default table correct on every sensor, and it is also what makes a
 * bare "0.5" unreadable. Each line turns the stored number back into something the operator
 * can check against their own sensor.
 */
const RMAD_FIELDS: FieldSpec[] = [
  {
    key: 'window',
    label: 'window',
    unit: 'próbek',
    help: (raw, ctx) => {
      const n = parseFloat(raw);
      if (!Number.isFinite(n) || ctx.medianIntervalSec == null) return null;
      return `≈ ${wallClockSpan(n * ctx.medianIntervalSec)} historii tego czujnika.`;
    },
    // §7 #14: the window is in SAMPLES, so two sensors on identical params have wildly
    // different clock memory — 720 samples is ~3 h at 15,3 s/próbkę and ~78 h at 391 s.
    warn: (raw, ctx) => {
      const n = parseFloat(raw);
      if (!Number.isFinite(n) || ctx.medianIntervalSec == null) return null;
      if (n * ctx.medianIntervalSec <= HOURS_48) return null;
      return `Ponad 48 h pamięci — czujnik zmienia się rzadko. Rozważ 240 próbek.`;
    },
  },
  {
    key: 'min_samples',
    label: 'min_samples',
    unit: 'próbek',
    help: (raw, ctx) => {
      const n = parseFloat(raw);
      if (!Number.isFinite(n)) return null;
      if (ctx.medianIntervalSec == null) return 'Tyle próbek przed pierwszym werdyktem.';
      return `Pierwszy werdykt po ≈ ${wallClockSpan(n * ctx.medianIntervalSec)}.`;
    },
  },
  {
    key: 'z_scale',
    label: 'z_scale',
    step: '0.1',
    help: () => 'Skala wyniku. Nie zmieniaj — próg robi to samo.',
  },
  {
    key: 'scale_floor',
    label: 'scale_floor',
    step: '0.1',
    help: (_raw, ctx) =>
      ctx.unitOfMeasurement
        ? `Minimalny rozrzut uznawany za normalny, w ${ctx.unitOfMeasurement}.`
        : 'Minimalny rozrzut uznawany za normalny, w jednostce czujnika.',
  },
  {
    key: 'high_threshold',
    label: 'high_threshold',
    step: '0.01',
    help: (raw, ctx) => {
      const t = parseFloat(raw);
      if (!Number.isFinite(t) || t <= 0 || t >= 1) return null;
      return `= odchylenie ${pl(robustZ(t, ctx.zScale))}σ (robust). Alarm powyżej.`;
    },
  },
  {
    key: 'low_threshold',
    label: 'low_threshold',
    step: '0.01',
    help: (raw, ctx) => {
      const t = parseFloat(raw);
      if (!Number.isFinite(t) || t < 0 || t >= 1) return null;
      return `= odchylenie ${pl(robustZ(t, ctx.zScale))}σ (robust). Alarm gaśnie poniżej.`;
    },
  },
  { key: 'min_consecutive', label: 'min_consecutive' },
  { key: 'frozen_window', label: 'frozen_window' },
  {
    key: 'frozen_variance_threshold',
    label: 'frozen_variance',
    step: '0.0001',
    span2: true,
    help: (raw) =>
      parseFloat(raw) === 0
        ? 'Wykrywanie zamarłego czujnika wyłączone (0 = nigdy nie zapala).'
        : null,
  },
];

const MAD_FIELDS: FieldSpec[] = [
  { key: 'threshold', label: 'threshold', step: '0.1' },
  { key: 'window', label: 'window' },
];

const STL_FIELDS: FieldSpec[] = [
  { key: 'period', label: 'period' },
  { key: 'seasonal', label: 'seasonal' },
  { key: 'threshold', label: 'threshold', step: '0.1', span2: true },
];

function fieldsFor(name: DetectorName): FieldSpec[] {
  switch (name) {
    case 'mad':
      return MAD_FIELDS;
    case 'stl':
      return STL_FIELDS;
    case 'hst':
      return HST_FIELDS;
    default:
      return RMAD_FIELDS;
  }
}

export function DetectorParamGrid({
  entityIdx,
  detIdx,
  detector,
  onParamChange,
  ctx = DEFAULT_CTX,
}: DetectorParamGridProps) {
  const fields = fieldsFor(detector.name);
  // A stored block may omit keys, and an omitted key means the server default is in force
  // (`params: {}` is the fresh-install shape). The grid therefore renders against the EFFECTIVE
  // params -- defaults under whatever the operator actually set -- while the input itself stays
  // empty for an omitted key and shows the default as its placeholder. Nothing is written back:
  // typing in the field is what makes a key exist. Same merge InputValidator does server-side.
  const defaults = defaultsFor(detector.name);
  const effective = { ...defaults, ...detector.params };
  const errors = validateDetectorParams(detector.name, effective);

  return (
    <div class="argus-param-grid">
      {fields.map((field) => {
        const inputId = `param-${entityIdx}-${detIdx}-${field.key}`;
        const error = errors[field.key];
        const raw = detector.params[field.key] ?? '';
        // Help and warn describe the value that is IN FORCE, which for an omitted key is the
        // default -- saying nothing there would hide the meaning of the value the sensor runs on.
        // Both are suppressed while the field is in error: restating what the value means when it
        // is not a legal value would read as confirmation.
        const shown = effective[field.key] ?? '';
        const help = error ? null : field.help?.(shown, ctx) ?? null;
        const warn = error ? null : field.warn?.(shown, ctx) ?? null;
        return (
          <div
            key={field.key}
            class={`argus-param-field${field.span2 ? ' argus-param-grid--span2' : ''}${
              error ? ' argus-param-field--error' : ''
            }`}
          >
            <label class="argus-param-field__label" for={inputId}>
              {field.label}
              {field.unit && <span class="argus-param-field__unit"> ({field.unit})</span>}
            </label>
            <Input
              id={inputId}
              value={raw}
              onChange={(v) => onParamChange(field.key, v)}
              type="number"
              step={field.step}
              placeholder={defaults[field.key]}
              invalid={!!error}
              ariaDescribedby={`${inputId}-err`}
              ariaLabel={field.label}
            />
            <FieldValidationError message={error} />
            {help && <p class="argus-param-field__help">{help}</p>}
            {warn && <p class="argus-param-field__warn">{warn}</p>}
          </div>
        );
      })}
    </div>
  );
}
