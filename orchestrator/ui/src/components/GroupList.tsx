import type { GroupConfig } from '../api/types';
import { GroupListRow } from './GroupListRow';

interface GroupListProps {
  groups: GroupConfig[];
}

// Replaces SensorList's role for groups — <ul class="argus-list"> of GroupListRow,
// with an EmptyState-style branch for zero groups (08-UI-SPEC.md "#/groups (Group List)").
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
    <ul class="argus-list">
      {groups.map((group) => (
        <GroupListRow key={group.groupId} group={group} />
      ))}
    </ul>
  );
}
