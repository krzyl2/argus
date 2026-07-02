import { useEffect, useRef, useState } from 'preact/hooks';
import { apiGet } from '../api/client';
import type { GroupStatus, GroupStatusResponse } from '../api/types';
import { AttributionBar } from './AttributionBar';

interface AttributionPanelProps {
  groupId: string;
}

// Polls roughly the batch interval cadence — no SSE (08-UI-SPEC.md Attribution Display
// Contract #5). 60s is a reasonable fixed interval independent of the exact batch cadence;
// staleness is bounded by the panel simply re-rendering the latest GroupStatus on each tick.
const POLL_INTERVAL_MS = 60_000;

// Ranked per-feature/per-member contribution list for a joint-multivariate group's last
// verdict (GRP-09). Only mounted on #/groups/:id (existing groups — GroupEditorForm gates
// this). Renders one of 4 states; never re-sorts contributions (server pre-sorts, RESEARCH
// Pitfall 4 fix already lands the sort in GroupStatusCache.Set).
export function AttributionPanel({ groupId }: AttributionPanelProps) {
  const [status, setStatus] = useState<GroupStatus | null>(null);
  const [loaded, setLoaded] = useState(false);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function poll() {
      try {
        const res = await apiGet<GroupStatusResponse>(`api/groups/${groupId}/status`);
        if (!cancelled) {
          setStatus(res.status);
          setLoaded(true);
        }
      } catch {
        // Network/parse errors leave the panel in its last-known state rather than
        // flashing an error — attribution is a soft, best-effort display.
        if (!cancelled) setLoaded(true);
      }
    }

    poll();
    intervalRef.current = setInterval(poll, POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [groupId]);

  if (!loaded) {
    return <p class="argus-label">Loading attribution…</p>;
  }

  if (!status) {
    return (
      <div class="argus-empty">
        <p class="argus-body">No anomaly score yet — attribution will appear after the next batch run.</p>
      </div>
    );
  }

  if (status.contributions.length === 0) {
    return <p class="argus-body argus-attribution-panel__unsupported">This algorithm does not provide per-feature attribution.</p>;
  }

  const topContribution = status.contributions[0].contribution;

  return (
    <div class="argus-attribution-panel">
      {status.contributions.map((c, idx) => (
        <AttributionBar
          key={c.memberId}
          memberId={c.memberId}
          contribution={c.contribution}
          topContribution={topContribution}
          topRank={idx === 0}
        />
      ))}
    </div>
  );
}
