import type { SensorEntry, DetectorEntry as DetectorEntryModel } from '../api/types';
import { DetectorDisclosure } from './DetectorDisclosure';
import { Checkbox } from './Checkbox';

interface SensorListRowProps {
  entry: SensorEntry;
  entityIdx: number;
  isTracked: boolean;
  detectors: DetectorEntryModel[];
  onToggleTracked: (checked: boolean) => void;
  onDetectorTypeChange: (detIdx: number, name: 'hst' | 'mad' | 'stl') => void;
  onDetectorParamChange: (detIdx: number, key: string, value: string) => void;
  onDetectorRemove: (detIdx: number) => void;
  onDetectorAdd: () => void;
}

// Replaces one <li class="argus-list-row"> in BuildListRows.
export function SensorListRow({
  entry,
  entityIdx,
  isTracked,
  detectors,
  onToggleTracked,
  onDetectorTypeChange,
  onDetectorParamChange,
  onDetectorRemove,
  onDetectorAdd,
}: SensorListRowProps) {
  // Friendly name only rendered when present AND different from entity_id (exact v3.0 rule).
  const showFriendlyName =
    !!entry.friendlyName && entry.friendlyName !== entry.entityId;

  const valueDisplay = entry.unitOfMeasurement
    ? `${entry.currentValue} ${entry.unitOfMeasurement}`
    : entry.currentValue;

  return (
    <li class={`argus-list-row${isTracked ? ' argus-list-row--tracked' : ''}`}>
      <label style={{ display: 'contents' }}>
        <Checkbox checked={isTracked} ariaLabel={entry.entityId} onChange={onToggleTracked} />
        <div class="argus-row-content">
          <span class="argus-row-entity-id">{entry.entityId}</span>
          {showFriendlyName && (
            <span class="argus-row-friendly-name">{entry.friendlyName}</span>
          )}
        </div>
        <div class="argus-row-meta">
          <span class="argus-row-value">{valueDisplay}</span>
          {isTracked && <span class="argus-pill argus-pill--tracked">tracked</span>}
        </div>
      </label>
      {isTracked && (
        <DetectorDisclosure
          entityId={entry.entityId}
          entityIdx={entityIdx}
          detectors={detectors}
          onTypeChange={onDetectorTypeChange}
          onParamChange={onDetectorParamChange}
          onRemove={onDetectorRemove}
          onAdd={onDetectorAdd}
        />
      )}
    </li>
  );
}
