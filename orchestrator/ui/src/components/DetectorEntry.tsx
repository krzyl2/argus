import type { DetectorEntry as DetectorEntryModel } from '../api/types';
import { DetectorParamGrid } from './DetectorParamGrid';
import { Select } from './Select';
import { Button } from './Button';

const DETECTOR_TYPE_OPTIONS = [
  { value: 'hst', label: 'HST' },
  { value: 'mad', label: 'MAD' },
  { value: 'stl', label: 'STL' },
];

interface DetectorEntryProps {
  entityIdx: number;
  detIdx: number;
  detector: DetectorEntryModel;
  onTypeChange: (name: 'hst' | 'mad' | 'stl') => void;
  onParamChange: (key: string, value: string) => void;
  onRemove: () => void;
}

// Replaces .argus-detector-entry / BuildDetectorEntry.
export function DetectorEntry({
  entityIdx,
  detIdx,
  detector,
  onTypeChange,
  onParamChange,
  onRemove,
}: DetectorEntryProps) {
  const timingCaption =
    detector.name === 'hst' ? 'streaming (live, ~2 s reload)' : 'batch (runs every N min)';

  return (
    <div class="argus-detector-entry">
      <div class="argus-detector-header">
        <Select
          value={detector.name}
          ariaLabel={`Detector type for entity ${entityIdx}`}
          options={DETECTOR_TYPE_OPTIONS}
          onChange={(v) => onTypeChange(v as 'hst' | 'mad' | 'stl')}
        />
        <span class="argus-timing-caption">{timingCaption}</span>
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
