import { useState } from 'preact/hooks';
import type { SensorEntry, GroupConfig } from '../api/types';
import { pendingPrefillMembers } from '../state/groups';

interface AreaSuggestionBannerProps {
  sensors: SensorEntry[];
  groups: GroupConfig[];
}

const MIN_AREA_SENSORS = 3;

/** The smallest area (by sensor count) with >=3 ungrouped sensors, or null if none qualify. */
function findSuggestion(
  sensors: SensorEntry[],
  groups: GroupConfig[]
): { area: string; entityIds: string[] } | null {
  const groupedIds = new Set(groups.flatMap((g) => g.members));
  const byArea = new Map<string, string[]>();
  for (const s of sensors) {
    if (!s.areaName || groupedIds.has(s.entityId)) continue;
    const bucket = byArea.get(s.areaName);
    if (bucket) bucket.push(s.entityId);
    else byArea.set(s.areaName, [s.entityId]);
  }
  for (const [area, entityIds] of byArea) {
    if (entityIds.length >= MIN_AREA_SENSORS) {
      return { area, entityIds };
    }
  }
  return null;
}

// "N sensors share area X — group them?" (SRCH-03), operator-approved only. "Review"
// pre-fills the /groups/new member picker (never pre-saves — pendingPrefillMembers is
// consumed once by resetDraft, the operator still edits mode/algorithm and explicitly
// saves). "Not now" dismisses for the session only (not persisted server-side — no new
// config surface for dismissal state, per 08-UI-SPEC.md).
export function AreaSuggestionBanner({ sensors, groups }: AreaSuggestionBannerProps) {
  const [dismissedAreas, setDismissedAreas] = useState<Set<string>>(new Set());

  const suggestion = findSuggestion(sensors, groups);
  if (!suggestion || dismissedAreas.has(suggestion.area)) {
    return null;
  }

  function review() {
    if (!suggestion) return;
    pendingPrefillMembers.value = suggestion.entityIds;
    location.hash = '#/groups/new';
  }

  function dismiss() {
    if (!suggestion) return;
    setDismissedAreas((prev) => new Set(prev).add(suggestion.area));
  }

  return (
    <div class="argus-banner argus-area-suggestion-banner" role="status">
      <span class="argus-body">
        {suggestion.entityIds.length} sensors share area &quot;{suggestion.area}&quot; — group them?
      </span>
      <button type="button" class="argus-btn argus-btn--primary" onClick={review}>
        Review
      </button>
      <button type="button" class="argus-btn" onClick={dismiss}>
        Not now
      </button>
    </div>
  );
}
