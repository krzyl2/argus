import { signal } from '@preact/signals';
import { apiGet, apiPost } from '../api/client';
import type {
  GroupConfig,
  GroupMode,
  GroupDetectorName,
  GroupSaveRequest,
  GroupSaveResponse,
  GroupStatus,
  GroupStatusResponse,
  SensorEntry,
} from '../api/types';
import { validateGroupMembers, validateUnitConsistency } from '../validation/groupParams';

export type SaveState = 'idle' | 'saving' | { result: GroupSaveResponse };

export const groups = signal<GroupConfig[]>([]);
export const loading = signal(false);
export const saveState = signal<SaveState>('idle');

// Last-known status per group id for the Detectors list (id -> GroupStatus | null).
// null means the backend returned 200 with no score yet ("Oczekuje"); a missing key
// means status has not been fetched at all (the row renders no status badge).
export const groupStatuses = signal<Record<string, GroupStatus | null>>({});

// Draft signals for the group editor (create + edit) — mirrors state/sensors.ts's
// entityEdits pattern, but a single group is edited at a time (one screen).
export const draftGroupId = signal('');
export const draftFriendlyName = signal('');
export const draftMembers = signal<string[]>([]);
export const draftMode = signal<GroupMode>('peer_divergence');
export const draftDetector = signal<GroupDetectorName | null>(null);
export const draftParams = signal<Record<string, string>>({});
// Tracks which preset label (Low/Med/High) is the active baseline for the "customized"
// indicator (ALGO-01/02) — null until a preset is picked. Params expanded from a preset
// stay self-contained in draftParams; this signal is UI-only bookkeeping for the
// "Med, customized" label and is never sent to the server.
export const draftPresetLabel = signal<string | null>(null);

// SRCH-03: entity ids to pre-fill the next /groups/new draft with, set by
// AreaSuggestionBanner's "Review" action just before navigating. Consumed (and cleared)
// by resetDraft on the next /groups/new mount — never auto-saved, operator still reviews
// and explicitly saves (AreaSuggestionBanner never calls saveGroup itself).
export const pendingPrefillMembers = signal<string[] | null>(null);

/** Resets all draft signals to their empty/default state (entering /groups/new). */
export function resetDraft(): void {
  draftGroupId.value = '';
  draftFriendlyName.value = '';
  draftMembers.value = pendingPrefillMembers.value ?? [];
  pendingPrefillMembers.value = null;
  draftMode.value = 'peer_divergence';
  draftDetector.value = null;
  draftParams.value = {};
  draftPresetLabel.value = null;
}

/** Loads an existing group into the draft signals (entering /groups/:id). */
export function loadDraftFromGroup(group: GroupConfig): void {
  draftGroupId.value = group.groupId;
  draftFriendlyName.value = group.friendlyName;
  draftMembers.value = [...group.members];
  draftMode.value = group.mode;
  draftDetector.value = group.detector;
  draftParams.value = { ...group.params };
  // Existing groups' saved params are self-contained (no preset label round-trips through
  // the backend) — the chooser starts with no preset baseline; AlgorithmChooser derives one
  // via SensitivityPresetPicker's initial-preset-matching so the label isn't always empty.
  draftPresetLabel.value = null;
}

// Monotonic request sequence — guards against out-of-order/racing loadGroups
// responses overwriting newer state with a stale one (same pattern as state/sensors.ts).
let loadGroupsSeq = 0;

export async function loadGroups(): Promise<void> {
  const seq = ++loadGroupsSeq;
  loading.value = true;
  try {
    const res = await apiGet<{ groups: GroupConfig[] }>('api/groups');
    if (seq !== loadGroupsSeq) return; // stale response — a newer request is in flight/done
    groups.value = res.groups;
  } finally {
    if (seq === loadGroupsSeq) loading.value = false;
  }
}

/**
 * Fetches each currently-loaded group's status in parallel and updates groupStatuses.
 * Per-group failures are tolerated: a failed fetch leaves that group's previous value
 * untouched (never overwrites a good status with an error). The new map is merged onto
 * the previous one so partial failures preserve earlier successful reads.
 */
export async function loadGroupStatuses(): Promise<void> {
  const current = groups.value;
  const results = await Promise.all(
    current.map(async (g) => {
      try {
        const res = await apiGet<GroupStatusResponse>(
          `api/groups/${encodeURIComponent(g.groupId)}/status`
        );
        return { groupId: g.groupId, status: res.status };
      } catch {
        // Tolerate a single group's failure — skip it (keep the previous value).
        return null;
      }
    })
  );
  const next: Record<string, GroupStatus | null> = { ...groupStatuses.value };
  for (const r of results) {
    if (r) next[r.groupId] = r.status;
  }
  groupStatuses.value = next;
}

/** Looks up a single group by id from the currently loaded list, or undefined if not found. */
export function findGroup(groupId: string): GroupConfig | undefined {
  return groups.value.find((g) => g.groupId === groupId);
}

/**
 * Validates the current draft's member floor + (peer mode) unit consistency.
 * Callers pass the resolved SensorEntry[] for the draft's selected member ids
 * (unit info lives on SensorEntry, not on the plain member id string) — mirrors
 * MemberPicker's need to look up units from the loaded sensors list.
 */
export function validateDraftMembers(memberEntries: SensorEntry[]): {
  memberFloorError: string | null;
  unitMismatchError: string | null;
} {
  return {
    memberFloorError: validateGroupMembers(draftMembers.value),
    unitMismatchError: validateUnitConsistency(memberEntries, draftMode.value),
  };
}

/**
 * Saves the full groups list (full-list-replace semantics — GroupSaveRequest.cs's
 * POST /api/groups/save always replaces the entire groups: key). Builds the request
 * from the current `groups` signal with the draft group upserted by groupId.
 */
export async function saveGroup(): Promise<void> {
  // CR-02: guided-flow suggestions are approve-only — nothing auto-applies
  // without an explicit pick. Refuse to save if the operator never chose an
  // algorithm, rather than silently defaulting to 'peer_divergence'. The UI
  // also disables Save via hasErrors/noAlgorithmError; this is defense in
  // depth for any other caller of saveGroup().
  if (draftDetector.value === null) {
    saveState.value = {
      result: { ok: false, kind: 'error', reason: 'Choose an algorithm to continue.' },
    };
    return;
  }
  saveState.value = 'saving';
  const draft: GroupConfig = {
    groupId: draftGroupId.value,
    friendlyName: draftFriendlyName.value,
    members: draftMembers.value,
    mode: draftMode.value,
    detector: draftDetector.value,
    params: draftParams.value,
  };
  const existingIdx = groups.value.findIndex((g) => g.groupId === draft.groupId);
  const nextGroups =
    existingIdx >= 0
      ? groups.value.map((g, i) => (i === existingIdx ? draft : g))
      : [...groups.value, draft];

  const body: GroupSaveRequest = { groups: nextGroups };
  try {
    const result = await apiPost<GroupSaveResponse>('api/groups/save', body);
    saveState.value = { result };
    if (result.ok) {
      await loadGroups();
    }
  } catch (err) {
    saveState.value = {
      result: { ok: false, kind: 'error', reason: err instanceof Error ? err.message : 'unexpected error' },
    };
  }
}

/**
 * Deletes a group via the full-list-replace save endpoint (no dedicated delete
 * endpoint exists — the backend only supports POST /api/groups/save with the
 * complete groups list). Posts the current list minus the target groupId, then
 * refreshes from the server on success. A groupId not present in the current
 * list is a no-op post of the unchanged list (never crashes).
 */
export async function deleteGroup(groupId: string): Promise<void> {
  saveState.value = 'saving';
  const nextGroups = groups.value.filter((g) => g.groupId !== groupId);
  const body: GroupSaveRequest = { groups: nextGroups };
  try {
    const result = await apiPost<GroupSaveResponse>('api/groups/save', body);
    saveState.value = { result };
    if (result.ok) {
      await loadGroups();
    }
  } catch (err) {
    saveState.value = {
      result: { ok: false, kind: 'error', reason: err instanceof Error ? err.message : 'unexpected error' },
    };
  }
}
