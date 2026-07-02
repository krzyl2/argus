import type { GroupConfig, GroupStatus } from '../api/types';

interface GroupListRowProps {
  group: GroupConfig;
  status?: GroupStatus | null;
}

// Replaces one <li class="argus-list-row"> analog for groups (SensorListRow's role-match).
// Status pill is optional here — populated by 08-04's AttributionPanel polling; renders a
// "no status yet" state when absent (this plan does not fetch /api/groups/{id}/status).
export function GroupListRow({ group, status }: GroupListRowProps) {
  const modeLabel = group.mode === 'peer_divergence' ? 'peer' : 'joint';
  const memberWord = group.members.length === 1 ? 'member' : 'members';

  return (
    <li class="argus-list-row">
      <div class="argus-row-content">
        <span class="argus-row-entity-id">{group.friendlyName || group.groupId}</span>
        <span class="argus-pill">{modeLabel}</span>
      </div>
      <div class="argus-row-meta">
        <span class="argus-label">
          {group.members.length} {memberWord}
        </span>
        {status ? (
          <span class="argus-pill argus-pill--tracked">
            {status.isAnomaly ? 'anomaly' : 'active'}
          </span>
        ) : (
          <span class="argus-label">no status yet</span>
        )}
        <a class="argus-label" href={`#/groups/${encodeURIComponent(group.groupId)}`}>
          Edit
        </a>
      </div>
    </li>
  );
}
