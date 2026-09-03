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
    <GroupRow group={row.group as NonNullable<DetectorRow['group']>} status={row.status} />
  ) : (
    <SensorRow entry={row.entry as NonNullable<DetectorRow['entry']>} />
  );
}

function GroupRow({
  group,
  status,
}: {
  group: NonNullable<DetectorRow['group']>;
  status?: DetectorRow['status'];
}) {
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
        {status !== undefined &&
          (status === null ? (
            <Badge tone="warn">Oczekuje</Badge>
          ) : status.isAnomaly === true ? (
            <Badge tone="error">Anomalia</Badge>
          ) : (
            <Badge tone="ok">Działa</Badge>
          ))}
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
  // "Warmed up" and "has a usable band" are different facts, and conflating them is what made
  // the old chip misleading: rmad reports warmed_up at min_samples, but until a verdict has
  // carried an expected/lower/upper the editor has no band to show and no threshold the
  // operator can sanity-check. Say "Kalibracja" for that state instead of a green "Działa".
  const hasBand = entry.calibratedUpper != null;
  // WS4/F9: sensor.zamrazarkapiwnica_power was scored at 0.996 while absent from HA's snapshot.
  // The row must appear and stay editable — but say plainly that HA no longer lists it, or the
  // operator reads a stale score as live. D8: Polish.
  const knownToHa = entry.knownToHa !== false;

  return (
    <li class="argus-list-row">
      <div class="argus-row-content">
        <span class="argus-row-entity-id">{entry.entityId}</span>
        {showFriendlyName && <span class="argus-row-friendly-name">{entry.friendlyName}</span>}
      </div>
      <div class="argus-row-meta">
        {hasWarmUpStatus &&
          (entry.warmedUp ? (
            hasBand ? (
              <Badge tone="ok">Działa</Badge>
            ) : (
              <Badge tone="warn">Kalibracja</Badge>
            )
          ) : (
            <Badge tone="warn">
              Rozgrzewka {entry.readingCount}/{entry.warmUpWindow}
            </Badge>
          ))}
        {!knownToHa && <Badge tone="warn">Nieznana w HA</Badge>}
        <Badge tone="tracked">tracked</Badge>
        <a class="argus-label" href={`#/detectors/sensor/${encodeURIComponent(entry.entityId)}`}>
          Edit
        </a>
      </div>
    </li>
  );
}
