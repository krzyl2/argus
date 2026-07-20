import { useEffect, useRef, useState } from 'preact/hooks';
import type { GroupConfig, GroupStatus } from '../api/types';
import { deleteGroup } from '../state/groups';
import { Button } from './Button';
import { Badge } from './Badge';

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
        <Badge tone="neutral">{modeLabel}</Badge>
        <Badge tone="accent">{group.detector}</Badge>
      </div>
      <div class="argus-row-meta">
        <span class="argus-label">
          {group.members.length} {memberWord}
        </span>
        {status ? (
          <Badge tone="tracked">{status.isAnomaly ? 'anomaly' : 'active'}</Badge>
        ) : (
          <span class="argus-label">no status yet</span>
        )}
        <a class="argus-label" href={`#/groups/${encodeURIComponent(group.groupId)}`}>
          Edit
        </a>
        <Button variant="destructive-ghost" size="xs" onClick={handleDeleteClick}>
          {armed ? 'Confirm delete' : 'Delete group'}
        </Button>
      </div>
    </li>
  );
}
