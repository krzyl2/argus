import type { SensorEntry, DetectorEntry as DetectorEntryModel, DetectorName } from '../api/types';
import { DetectorDisclosure } from './DetectorDisclosure';
import { Checkbox } from './Checkbox';
import { Badge } from './Badge';

interface SensorListRowProps {
  entry: SensorEntry;
  entityIdx: number;
  isTracked: boolean;
  isSelected: boolean;
  onSelectRow: () => void;
  detectors: DetectorEntryModel[];
  onToggleTracked: (checked: boolean) => void;
  onDetectorTypeChange: (detIdx: number, name: DetectorName) => void;
  onDetectorParamChange: (detIdx: number, key: string, value: string) => void;
  onDetectorRemove: (detIdx: number) => void;
  onDetectorAdd: () => void;
}

// Replaces one <li class="argus-list-row"> in BuildListRows.
// D-04: single-select-and-expand — clicking the row selects it; only the selected
// AND tracked row expands its detector editor inline.
export function SensorListRow({
  entry,
  entityIdx,
  isTracked,
  isSelected,
  onSelectRow,
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

  // WS4/F9: an entity Argus tracks that HA does not list. The row stays fully interactive —
  // it must be possible to untick and to edit its detector — but it must not pretend to have a
  // reading. D8: operator-facing text is Polish.
  const knownToHa = entry.knownToHa !== false;
  const valueDisplay =
    entry.currentValue == null
      ? '—'
      : entry.unitOfMeasurement
        ? `${entry.currentValue} ${entry.unitOfMeasurement}`
        : entry.currentValue;

  return (
    <li
      class={`argus-list-row${isTracked ? ' argus-list-row--tracked' : ''}${
        isSelected ? ' argus-list-row--selected' : ''
      }`}
      onClick={onSelectRow}
    >
      <span onClick={(e) => e.stopPropagation()}>
        <Checkbox checked={isTracked} ariaLabel={entry.entityId} onChange={onToggleTracked} />
      </span>
      <div class="argus-row-content">
        <span class="argus-row-entity-id">{entry.entityId}</span>
        {showFriendlyName && (
          <span class="argus-row-friendly-name">{entry.friendlyName}</span>
        )}
      </div>
      <div class="argus-row-meta">
        <span class="argus-row-value">{valueDisplay}</span>
        {!knownToHa && <Badge tone="warn">Nieznana w HA</Badge>}
        {isTracked && <Badge tone="tracked">tracked</Badge>}
      </div>
      {isSelected && isTracked && (
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
