import type { GroupConfig } from '../api/types';
import { GroupListRow } from './GroupListRow';
import { Card } from './Card';

interface GroupListProps {
  groups: GroupConfig[];
}

// Replaces SensorList's role for groups — Card-wrapped <ul class="argus-list"> of
// GroupListRow (D-02), with a custom .argus-empty branch for zero groups (NOT the
// sensor-specific EmptyState — its prop shape is query-based and doesn't apply here).
export function GroupList({ groups }: GroupListProps) {
  if (groups.length === 0) {
    return (
      <div class="argus-empty">
        <p class="argus-body">No groups configured.</p>
        <p class="argus-label">
          Groups let you detect anomalies across related sensors — divergence within a group, or
          jointly-abnormal combinations. Create your first group to get started.
        </p>
      </div>
    );
  }

  return (
    <Card padding="none">
      <ul class="argus-list">
        {groups.map((group) => (
          <GroupListRow key={group.groupId} group={group} />
        ))}
      </ul>
    </Card>
  );
}
