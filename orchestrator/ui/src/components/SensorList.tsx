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
  // SRCH-02: when true, render one collapsible <details> section per HA area
  // (alphabetical, domain/"Ungrouped" fallback last) instead of one flat <ul>.
  // Default (false/omitted) mode is unchanged — #/sensors is untouched.
  groupByArea?: boolean;
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
  groupByArea = false,
}: SensorListProps) {
  if (entries.length === 0) {
    return <EmptyState query={query} />;
  }

  // trackedEntityIdx: entity's 0-based position among tracked entries, matching the
  // save-handler's alphabetical-sort correlation (see EntityPickerPage.cs BuildListRows).
  let trackedEntityIdx = 0;

  function renderRow(entry: SensorEntry) {
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
  }

  if (!groupByArea) {
    return <ul class="argus-list">{entries.map(renderRow)}</ul>;
  }

  // Group by resolved area name; entries with no area fall back to a per-domain
  // section (SRCH-02: "a final domain/'Ungrouped' fallback section").
  const byArea = new Map<string, SensorEntry[]>();
  for (const entry of entries) {
    const key = entry.areaName ?? `__domain__:${entry.domain || 'Ungrouped'}`;
    const bucket = byArea.get(key);
    if (bucket) bucket.push(entry);
    else byArea.set(key, [entry]);
  }

  // Alphabetical by area name; domain/"Ungrouped" fallback sections sort last,
  // among themselves alphabetically by domain.
  const sectionKeys = Array.from(byArea.keys()).sort((a, b) => {
    const aIsFallback = a.startsWith('__domain__:');
    const bIsFallback = b.startsWith('__domain__:');
    if (aIsFallback !== bIsFallback) return aIsFallback ? 1 : -1;
    return a.localeCompare(b);
  });

  return (
    <div class="argus-sensor-list-grouped">
      {sectionKeys.map((key) => {
        const sectionEntries = byArea.get(key)!;
        const label = key.startsWith('__domain__:') ? key.slice('__domain__:'.length) : key;
        return (
          <details key={key} open>
            <summary class="argus-disclosure-toggle">
              {label} ({sectionEntries.length})
            </summary>
            <ul class="argus-list">{sectionEntries.map(renderRow)}</ul>
          </details>
        );
      })}
    </div>
  );
}
