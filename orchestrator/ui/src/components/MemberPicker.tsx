import type { SensorEntry, GroupMode } from '../api/types';
import { SensorSearchInput } from './SensorSearchInput';
import { EmptyState } from './EmptyState';
import { FieldValidationError } from './FieldValidationError';
import { matchesSensorQuery } from './sensorMatch';
import { validateGroupMembers, validateUnitConsistency } from '../validation/groupParams';
import { Card } from './Card';
import { Checkbox } from './Checkbox';
import { Badge } from './Badge';

interface MemberPickerProps {
  sensors: SensorEntry[];
  selectedIds: string[];
  mode: GroupMode;
  query: string;
  onQueryChange: (q: string) => void;
  onToggleMember: (entityId: string, checked: boolean) => void;
}

// Wraps sensor rows in multi-select mode (checkbox semantics identical to the
// entity-tracked toggle in SensorListRow, without the detector-disclosure UI which
// does not apply to member selection). Reuses .argus-list/.argus-list-row/.argus-checkbox
// verbatim — same visual language as SensorList, no new CSS.
// Below this length the full sensor list (400+ on a typical HA install) would render
// unfiltered — require a search term before showing any rows.
const MIN_QUERY_LENGTH = 2;

export function MemberPicker({
  sensors,
  selectedIds,
  mode,
  query,
  onQueryChange,
  onToggleMember,
}: MemberPickerProps) {
  const queryTooShort = query.trim().length < MIN_QUERY_LENGTH;
  const filtered = queryTooShort ? [] : sensors.filter((s) => matchesSensorQuery(s, query));
  const selectedSet = new Set(selectedIds);
  const selectedEntries = sensors.filter((s) => selectedSet.has(s.entityId));

  const memberFloorError = validateGroupMembers(selectedIds);
  const unitMismatchError = validateUnitConsistency(selectedEntries, mode);

  return (
    <div class="argus-member-picker">
      <SensorSearchInput value={query} onChange={onQueryChange} />
      {queryTooShort ? (
        <div class="argus-empty">
          <p class="argus-label">Type at least {MIN_QUERY_LENGTH} characters to search sensors.</p>
        </div>
      ) : filtered.length === 0 ? (
        <EmptyState query={query} />
      ) : (
        <Card padding="none">
          <ul class="argus-list">
            {filtered.map((entry) => {
              const checked = selectedSet.has(entry.entityId);
              const showFriendlyName = !!entry.friendlyName && entry.friendlyName !== entry.entityId;
              return (
                <li key={entry.entityId} class={`argus-list-row${checked ? ' argus-list-row--tracked' : ''}`}>
                  <label style={{ display: 'contents' }}>
                    <Checkbox
                      checked={checked}
                      ariaLabel={entry.entityId}
                      onChange={(next) => onToggleMember(entry.entityId, next)}
                    />
                    <div class="argus-row-content">
                      <span class="argus-row-entity-id">{entry.entityId}</span>
                      {showFriendlyName && <span class="argus-row-friendly-name">{entry.friendlyName}</span>}
                    </div>
                    <div class="argus-row-meta">
                      {entry.unitOfMeasurement && (
                        <span class="argus-row-value">{entry.unitOfMeasurement}</span>
                      )}
                      {checked && <Badge tone="member">member</Badge>}
                    </div>
                  </label>
                </li>
              );
            })}
          </ul>
        </Card>
      )}
      <FieldValidationError message={memberFloorError ?? undefined} />
      <FieldValidationError message={unitMismatchError ?? undefined} />
    </div>
  );
}

// Exported for GroupEditorForm's save-disabled computation without duplicating
// the picker's own validation call (single source of truth for the draft's
// member-floor/unit-mismatch status).
export function useMemberPickerValidation(
  selectedIds: string[],
  sensors: SensorEntry[],
  mode: GroupMode
): { memberFloorError: string | null; unitMismatchError: string | null } {
  const selectedSet = new Set(selectedIds);
  const selectedEntries = sensors.filter((s) => selectedSet.has(s.entityId));
  return {
    memberFloorError: validateGroupMembers(selectedIds),
    unitMismatchError: validateUnitConsistency(selectedEntries, mode),
  };
}
