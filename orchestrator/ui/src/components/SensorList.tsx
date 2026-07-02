import type { SensorEntry } from '../api/types';
import type { EntityEditState } from '../state/sensors';
import { SensorListRow } from './SensorListRow';
import { EmptyState } from './EmptyState';

interface SensorListProps {
  entries: SensorEntry[];
  query: string;
  edits: Record<string, EntityEditState>;
  onToggleTracked: (entityId: string, checked: boolean) => void;
  onDetectorTypeChange: (entityId: string, detIdx: number, name: 'hst' | 'mad' | 'stl') => void;
  onDetectorParamChange: (entityId: string, detIdx: number, key: string, value: string) => void;
  onDetectorRemove: (entityId: string, detIdx: number) => void;
  onDetectorAdd: (entityId: string) => void;
}

// Replaces #argus-sensor-list + BuildListRows.
export function SensorList({
  entries,
  query,
  edits,
  onToggleTracked,
  onDetectorTypeChange,
  onDetectorParamChange,
  onDetectorRemove,
  onDetectorAdd,
}: SensorListProps) {
  if (entries.length === 0) {
    return <EmptyState query={query} />;
  }

  // trackedEntityIdx: entity's 0-based position among tracked entries, matching the
  // save-handler's alphabetical-sort correlation (see EntityPickerPage.cs BuildListRows).
  let trackedEntityIdx = 0;

  return (
    <ul class="argus-list">
      {entries.map((entry) => {
        const edit = edits[entry.entityId];
        const isTracked = edit?.isTracked ?? entry.isTracked;
        const detectors = edit?.detectors ?? [];
        const entityIdx = isTracked ? trackedEntityIdx++ : -1;

        return (
          <SensorListRow
            key={entry.entityId}
            entry={entry}
            entityIdx={entityIdx}
            isTracked={isTracked}
            detectors={detectors}
            onToggleTracked={(checked) => onToggleTracked(entry.entityId, checked)}
            onDetectorTypeChange={(detIdx, name) => onDetectorTypeChange(entry.entityId, detIdx, name)}
            onDetectorParamChange={(detIdx, key, value) =>
              onDetectorParamChange(entry.entityId, detIdx, key, value)
            }
            onDetectorRemove={(detIdx) => onDetectorRemove(entry.entityId, detIdx)}
            onDetectorAdd={() => onDetectorAdd(entry.entityId)}
          />
        );
      })}
    </ul>
  );
}
