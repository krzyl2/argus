import { useEffect, useRef, useState } from 'preact/hooks';
import type { SensorEntry } from '../api/types';
import {
  draftGroupId,
  draftFriendlyName,
  draftMembers,
  draftMode,
  draftDetector,
  saveState,
  resetDraft,
  loadDraftFromGroup,
  findGroup,
  saveGroup,
  deleteGroup,
} from '../state/groups';
import { MemberPicker, useMemberPickerValidation } from './MemberPicker';
import { AlgorithmChooser } from './AlgorithmChooser';
import { AttributionPanel } from './AttributionPanel';
import { SaveBar } from './SaveBar';
import { GroupSaveResultBanner } from './GroupSaveResultBanner';
import { FieldValidationError } from './FieldValidationError';
import { Input } from './Input';
import { Select } from './Select';
import { Button } from './Button';
import { Card } from './Card';
import { Badge } from './Badge';

interface GroupEditorFormProps {
  groupId: string | null; // null = /groups/new
  sensors: SensorEntry[];
}

const DELETE_CONFIRM_WINDOW_MS = 3000;

function slugify(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '');
}

// Top-level create/edit form. The AlgorithmChooser mount point (08-04) is a plain
// slot below the member picker — this plan wires the draftDetector signal it will
// read/write, but ships no chooser UI itself (plan scope: group authoring end-to-end
// for peer/joint mode selection with a manual detector value already settable).
export function GroupEditorForm({ groupId, sensors }: GroupEditorFormProps) {
  useEffect(() => {
    if (groupId) {
      const existing = findGroup(groupId);
      if (existing) loadDraftFromGroup(existing);
    } else {
      resetDraft();
    }
  }, [groupId]);

  // Local search query for the member picker — independent of SensorsPage's query signal.
  const [memberQuery, setMemberQuery] = useState('');

  const { memberFloorError, unitMismatchError } = useMemberPickerValidation(
    draftMembers.value,
    sensors,
    draftMode.value
  );
  const nameError = draftFriendlyName.value.trim() === '' ? 'Must provide a value.' : null;
  const noAlgorithmError = draftDetector.value === null ? 'Choose an algorithm to continue.' : null;

  const saving = saveState.value === 'saving';
  const result = typeof saveState.value === 'object' ? saveState.value.result : null;
  const hasErrors = !!memberFloorError || !!unitMismatchError || !!nameError || !!noAlgorithmError;

  function toggleMember(entityId: string, checked: boolean) {
    draftMembers.value = checked
      ? [...draftMembers.value, entityId]
      : draftMembers.value.filter((id) => id !== entityId);
  }

  // Always-visible list of the draft's current members, so the operator sees which
  // sensors belong to the group without first typing a search query into MemberPicker.
  const selectedMembers = sensors.filter((s) => draftMembers.value.includes(s.entityId));

  // Inline two-step delete confirm — same copywriting contract as GroupListRow
  // ("Delete group" -> "Confirm delete" on a second click within ~3s, reverting if the
  // second click never comes; never window.confirm()). It lives on the editor rather than
  // the list row because Phase 14's D-01 redirect (/groups -> /detectors) made
  // GroupListRow unreachable, and DetectorListRow deliberately only navigates — so this
  // is the group's sole reachable delete affordance.
  const [deleteArmed, setDeleteArmed] = useState(false);
  const deleteTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (deleteTimerRef.current) clearTimeout(deleteTimerRef.current);
    },
    []
  );

  // deleteGroup POSTs the full groups list minus this id (the backend has no dedicated
  // delete endpoint), so firing it against a not-yet-loaded — i.e. empty — groups signal
  // would wipe every other group. Only offer delete once this group is actually present
  // in the loaded list.
  const deletable = groupId !== null && findGroup(groupId) !== undefined;

  async function handleDeleteClick() {
    if (!groupId) return;
    if (!deleteArmed) {
      setDeleteArmed(true);
      deleteTimerRef.current = setTimeout(() => setDeleteArmed(false), DELETE_CONFIRM_WINDOW_MS);
      return;
    }
    if (deleteTimerRef.current) clearTimeout(deleteTimerRef.current);
    setDeleteArmed(false);
    await deleteGroup(groupId);
    // Leave the editor only on a confirmed server-side delete — on failure the operator
    // stays put and GroupSaveResultBanner explains why.
    const state = saveState.value;
    if (typeof state === 'object' && state.result.ok) {
      location.hash = '#/detectors';
    }
  }

  return (
    <div>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">{groupId ? 'Edit group' : 'Create group'}</h1>
        <Button variant="ghost" size="sm" onClick={() => { location.hash = '#/groups'; }}>
          Back to groups
        </Button>
      </header>

      <div class={`argus-param-field${nameError ? ' argus-param-field--error' : ''}`}>
        <label class="argus-param-field__label" for="group-name">
          Name
        </label>
        <Input
          id="group-name"
          value={draftFriendlyName.value}
          onChange={(next) => {
            draftFriendlyName.value = next;
            if (!groupId) {
              draftGroupId.value = slugify(next);
            }
          }}
          invalid={!!nameError}
          ariaDescribedby={nameError ? 'group-name-err' : undefined}
        />
        <FieldValidationError message={nameError ?? undefined} />
      </div>

      <div class="argus-param-field">
        <label class="argus-param-field__label" for="group-mode">
          Mode
        </label>
        <Select
          value={draftMode.value}
          onChange={(v) => {
            draftMode.value = v as typeof draftMode.value;
          }}
          ariaLabel="Mode"
          options={[
            { value: 'peer_divergence', label: 'Peer-divergence — which sensor is diverging' },
            { value: 'joint', label: 'Joint (multivariate) — unusual combination' },
          ]}
        />
      </div>

      <p class="argus-section-label">Members</p>
      {selectedMembers.length > 0 && (
        <>
          <p class="argus-section-label">Selected ({selectedMembers.length})</p>
          <Card padding="none">
            <ul class="argus-list">
              {selectedMembers.map((entry) => {
                const showFriendlyName =
                  !!entry.friendlyName && entry.friendlyName !== entry.entityId;
                return (
                  <li key={entry.entityId} class="argus-list-row argus-list-row--tracked">
                    <div class="argus-row-content">
                      <span class="argus-row-entity-id">{entry.entityId}</span>
                      {showFriendlyName && (
                        <span class="argus-row-friendly-name">{entry.friendlyName}</span>
                      )}
                    </div>
                    <div class="argus-row-meta">
                      {entry.unitOfMeasurement && (
                        <span class="argus-row-value">{entry.unitOfMeasurement}</span>
                      )}
                      <Badge tone="member">member</Badge>
                      <Button
                        variant="destructive-ghost"
                        size="xs"
                        onClick={() => toggleMember(entry.entityId, false)}
                      >
                        Remove
                      </Button>
                    </div>
                  </li>
                );
              })}
            </ul>
          </Card>
        </>
      )}
      <MemberPicker
        sensors={sensors}
        selectedIds={draftMembers.value}
        mode={draftMode.value}
        query={memberQuery}
        onQueryChange={setMemberQuery}
        onToggleMember={toggleMember}
      />

      <p class="argus-section-label">Choose algorithm</p>
      <div id="algorithm-chooser-slot">
        <AlgorithmChooser existingDetector={groupId ? draftDetector.value : null} />
      </div>
      <FieldValidationError message={noAlgorithmError ?? undefined} />

      {groupId && <AttributionPanel groupId={groupId} />}

      {deletable && (
        <Button variant="destructive-ghost" size="xs" disabled={saving} onClick={handleDeleteClick}>
          {deleteArmed ? 'Confirm delete' : 'Delete group'}
        </Button>
      )}

      <SaveBar saving={saving} disabled={saving || hasErrors} onSave={saveGroup} />

      {result && <GroupSaveResultBanner result={result} memberCount={draftMembers.value.length} />}
    </div>
  );
}
