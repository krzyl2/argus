import type { DetectorEntry as DetectorEntryModel } from '../api/types';
import { DetectorEntry } from './DetectorEntry';
import { AddDetectorButton } from './AddDetectorButton';

interface DetectorDisclosureProps {
  entityId: string;
  entityIdx: number;
  detectors: DetectorEntryModel[];
  onTypeChange: (detIdx: number, name: 'hst' | 'mad' | 'stl') => void;
  onParamChange: (detIdx: number, key: string, value: string) => void;
  onRemove: (detIdx: number) => void;
  onAdd: () => void;
}

// Replaces <details class="argus-detectors-details"> / BuildDetectorDisclosure.
// Native <details>/<summary> preserved — no JS-driven open state needed.
export function DetectorDisclosure({
  entityId,
  entityIdx,
  detectors,
  onTypeChange,
  onParamChange,
  onRemove,
  onAdd,
}: DetectorDisclosureProps) {
  const summaryText = detectors.length > 0 ? `Detectors (${detectors.length})` : 'Detectors (none)';

  return (
    <details class="argus-detectors-details">
      <summary class="argus-disclosure-toggle">{summaryText}</summary>
      <div class="argus-detectors-panel">
        {detectors.map((detector, detIdx) => (
          <DetectorEntry
            key={detIdx}
            entityIdx={entityIdx}
            detIdx={detIdx}
            detector={detector}
            onTypeChange={(name) => onTypeChange(detIdx, name)}
            onParamChange={(key, value) => onParamChange(detIdx, key, value)}
            onRemove={() => onRemove(detIdx)}
          />
        ))}
        <AddDetectorButton entityId={entityId} onAdd={onAdd} />
      </div>
    </details>
  );
}
