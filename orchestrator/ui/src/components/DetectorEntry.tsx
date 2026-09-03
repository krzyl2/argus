import type { DetectorEntry as DetectorEntryModel, DetectorName } from '../api/types';
import { DetectorParamGrid, type FieldCtx } from './DetectorParamGrid';
import { AlgorithmCard } from './AlgorithmCard';
import { Button } from './Button';

// Client-hardcoded (no backend catalog for single-sensor detectors — see 12-CONTEXT.md
// Deferred). rmad is listed FIRST because it is the default (D-A).
//
// The hst copy is deliberately blunt (D-F): hst scores RARITY, not deviation, so on a
// quantized series a rare-but-perfectly-normal level outscores the modal one (F4), and its
// unbounded normalizer collapses the normal band after a single spike (F5). It is kept as the
// rollback path, not as an equal-quality alternative — nobody should pick it by accident.
const DETECTOR_TYPES: { name: DetectorName; bestFor: string }[] = [
  {
    name: 'rmad',
    bestFor: 'streaming (live) — odchylenie od własnej normy czujnika; domyślny',
  },
  {
    name: 'hst',
    bestFor:
      'streaming (live) — rzadkość wartości; legacy / niekalibrowany, wymaga ręcznego strojenia progów',
  },
  { name: 'mad', bestFor: 'batch (runs every N min)' },
  { name: 'stl', bestFor: 'batch (runs every N min)' },
];

interface DetectorEntryProps {
  entityIdx: number;
  detIdx: number;
  detector: DetectorEntryModel;
  onTypeChange: (name: DetectorName) => void;
  onParamChange: (key: string, value: string) => void;
  onRemove: () => void;
  // WR-06: identifies the entity in the ARIA label (e.g. entityId). Falls back to
  // `entity ${entityIdx}` when omitted, preserving prior callers' behavior.
  entityLabel?: string;
  /** Forwarded to the param grid so help lines can be written in this sensor's own terms. */
  ctx?: FieldCtx;
}

// Replaces .argus-detector-entry / BuildDetectorEntry.
export function DetectorEntry({
  entityIdx,
  detIdx,
  detector,
  onTypeChange,
  onParamChange,
  onRemove,
  entityLabel,
  ctx,
}: DetectorEntryProps) {
  return (
    <div class="argus-detector-entry">
      <div class="argus-detector-header">
        <div
          class="argus-algorithm-chooser__grid"
          role="radiogroup"
          aria-label={`Detector type for ${entityLabel ?? `entity ${entityIdx}`}`}
        >
          {DETECTOR_TYPES.map((t) => (
            <AlgorithmCard
              key={t.name}
              name={t.name}
              bestFor={t.bestFor}
              selected={detector.name === t.name}
              recommended={false}
              onSelect={(name) => onTypeChange(name as DetectorName)}
            />
          ))}
        </div>
        <Button variant="destructive-ghost" size="xs" ariaLabel="Remove this detector" onClick={onRemove}>
          Remove
        </Button>
      </div>
      <DetectorParamGrid
        entityIdx={entityIdx}
        detIdx={detIdx}
        detector={detector}
        onParamChange={onParamChange}
        ctx={ctx}
      />
    </div>
  );
}
