import type { DetectorEntry as DetectorEntryModel } from '../api/types';
import { DetectorParamGrid } from './DetectorParamGrid';
import { AlgorithmCard } from './AlgorithmCard';
import { Button } from './Button';

// Client-hardcoded (no backend catalog for single-sensor detectors — see 12-CONTEXT.md
// Deferred). bestFor text reuses the previous timingCaption wording verbatim (Assumption A1).
const DETECTOR_TYPES: { name: 'hst' | 'mad' | 'stl'; bestFor: string }[] = [
  { name: 'hst', bestFor: 'streaming (live, ~2 s reload)' },
  { name: 'mad', bestFor: 'batch (runs every N min)' },
  { name: 'stl', bestFor: 'batch (runs every N min)' },
];

interface DetectorEntryProps {
  entityIdx: number;
  detIdx: number;
  detector: DetectorEntryModel;
  onTypeChange: (name: 'hst' | 'mad' | 'stl') => void;
  onParamChange: (key: string, value: string) => void;
  onRemove: () => void;
  // WR-06: identifies the entity in the ARIA label (e.g. entityId). Falls back to
  // `entity ${entityIdx}` when omitted, preserving prior callers' behavior.
  entityLabel?: string;
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
              onSelect={(name) => onTypeChange(name as 'hst' | 'mad' | 'stl')}
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
      />
    </div>
  );
}
