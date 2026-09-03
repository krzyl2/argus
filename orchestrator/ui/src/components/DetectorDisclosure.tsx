import type { DetectorEntry as DetectorEntryModel, DetectorName } from '../api/types';
import { DetectorEntry } from './DetectorEntry';
import type { FieldCtx } from './DetectorParamGrid';
import { AddDetectorButton } from './AddDetectorButton';

interface DetectorDisclosureProps {
  entityId: string;
  entityIdx: number;
  detectors: DetectorEntryModel[];
  onTypeChange: (detIdx: number, name: DetectorName) => void;
  onParamChange: (detIdx: number, key: string, value: string) => void;
  onRemove: (detIdx: number) => void;
  onAdd: () => void;
  // WR-06: forwarded to DetectorEntry's ARIA label; entityIdx is kept for DOM-id
  // uniqueness only.
  entityLabel?: string;
  /** Per-sensor context for the param help lines; omitted on the list-row call site. */
  ctx?: FieldCtx;
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
  entityLabel,
  ctx,
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
            entityLabel={entityLabel}
            ctx={ctx}
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
