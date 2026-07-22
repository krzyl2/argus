import type { DetectorRow } from '../state/detectors';
import { Badge } from './Badge';

interface DetectorListRowProps {
  row: DetectorRow;
}

// D-03/D-08a: two thin row variants under one unified list, relocating GroupListRow's/
// SensorListRow's existing JSX verbatim minus their destructive/inline-expand affordances.
// Rows only navigate here — group delete (unchanged) and sensor untrack both live inside
// their respective editors, never on this list row.
export function DetectorListRow({ row }: DetectorListRowProps) {
  return row.kind === 'group' ? (
    <GroupRow group={row.group as NonNullable<DetectorRow['group']>} />
  ) : (
    <SensorRow entry={row.entry as NonNullable<DetectorRow['entry']>} />
  );
}

function GroupRow({ group }: { group: NonNullable<DetectorRow['group']> }) {
  const modeLabel = group.mode === 'peer_divergence' ? 'peer' : 'joint';
  const memberWord = group.members.length === 1 ? 'member' : 'members';

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
        <a class="argus-label" href={`#/groups/${encodeURIComponent(group.groupId)}`}>
          Edit
        </a>
      </div>
    </li>
  );
}

function SensorRow({ entry }: { entry: NonNullable<DetectorRow['entry']> }) {
  const showFriendlyName = !!entry.friendlyName && entry.friendlyName !== entry.entityId;
  // QUICK-warmup-status: chip renders only once the pipeline has scored this entity at
  // least once (readingCount/warmUpWindow both present) — untracked and no-status rows
  // never show a warm-up chip.
  const hasWarmUpStatus = entry.readingCount != null && entry.warmUpWindow != null;

  return (
    <li class="argus-list-row">
      <div class="argus-row-content">
        <span class="argus-row-entity-id">{entry.entityId}</span>
        {showFriendlyName && <span class="argus-row-friendly-name">{entry.friendlyName}</span>}
      </div>
      <div class="argus-row-meta">
        {hasWarmUpStatus &&
          (entry.warmedUp ? (
            <Badge tone="ok">Działa</Badge>
          ) : (
            <Badge tone="warn">
              Rozgrzewka {entry.readingCount}/{entry.warmUpWindow}
            </Badge>
          ))}
        <Badge tone="tracked">tracked</Badge>
        <a class="argus-label" href={`#/detectors/sensor/${encodeURIComponent(entry.entityId)}`}>
          Edit
        </a>
      </div>
    </li>
  );
}
