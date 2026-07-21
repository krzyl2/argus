import type { DetectorRow } from '../state/detectors';
import { DetectorListRow } from './DetectorListRow';
import { Card } from './Card';

interface DetectorListProps {
  rows: DetectorRow[];
}

// D-03: unified list merging group + tracked-sensor rows — Card-wrapped
// <ul class="argus-list"> (GroupList's structural analog), with a custom
// .argus-empty branch for the zero-rows case (not the query-based sensor EmptyState).
export function DetectorList({ rows }: DetectorListProps) {
  if (rows.length === 0) {
    return (
      <div class="argus-empty">
        <p class="argus-body">No detectors configured.</p>
        <p class="argus-label">
          Add a group or track a sensor to start detecting anomalies.
        </p>
      </div>
    );
  }

  return (
    <Card padding="none">
      <ul class="argus-list">
        {rows.map((row) => (
          <DetectorListRow key={row.key} row={row} />
        ))}
      </ul>
    </Card>
  );
}
