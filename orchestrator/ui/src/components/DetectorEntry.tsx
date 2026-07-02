import type { DetectorEntry as DetectorEntryModel } from '../api/types';
import { DetectorParamGrid } from './DetectorParamGrid';

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
        <select
          class="argus-detector-select"
          aria-label={`Detector type for entity ${entityIdx}`}
          value={detector.name}
          onChange={(e) => onTypeChange((e.target as HTMLSelectElement).value as 'hst' | 'mad' | 'stl')}
        >
          <option value="hst">HST</option>
          <option value="mad">MAD</option>
          <option value="stl">STL</option>
        </select>
        <span class="argus-timing-caption">{timingCaption}</span>
        <button
          type="button"
          class="argus-btn argus-btn--destructive-ghost"
          aria-label="Remove this detector"
          onClick={onRemove}
        >
          Remove
        </button>
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
