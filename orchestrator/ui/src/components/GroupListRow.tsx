import { useEffect, useRef, useState } from 'preact/hooks';
import type { GroupConfig, GroupStatus } from '../api/types';
import { deleteGroup } from '../state/groups';

interface GroupListRowProps {
  group: GroupConfig;
  status?: GroupStatus | null;
}

const CONFIRM_WINDOW_MS = 3000;

// Replaces one <li class="argus-list-row"> analog for groups (SensorListRow's role-match).
// Status pill is optional here — populated by 08-04's AttributionPanel polling; renders a
// "no status yet" state when absent (this plan does not fetch /api/groups/{id}/status).
export function GroupListRow({ group, status }: GroupListRowProps) {
  const modeLabel = group.mode === 'peer_divergence' ? 'peer' : 'joint';
  const memberWord = group.members.length === 1 ? 'member' : 'members';

  // Inline two-step delete confirm (08-UI-SPEC.md Copywriting Contract — "Delete group" ->
  // "Confirm delete" on a second click within ~3s, revert if not clicked; NO window.confirm()).
  const [armed, setArmed] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    },
    []
  );

  function handleDeleteClick() {
    if (armed) {
      if (timerRef.current) clearTimeout(timerRef.current);
      setArmed(false);
      deleteGroup(group.groupId);
      return;
    }
    setArmed(true);
    timerRef.current = setTimeout(() => setArmed(false), CONFIRM_WINDOW_MS);
  }

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
        <button
          type="button"
          class="argus-btn argus-btn--destructive-ghost"
          onClick={handleDeleteClick}
        >
          {armed ? 'Confirm delete' : 'Delete group'}
        </button>
      </div>
    </li>
  );
}
