import { useEffect, useState } from 'preact/hooks';
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
} from '../state/groups';
import { MemberPicker, useMemberPickerValidation } from './MemberPicker';
import { AlgorithmChooser } from './AlgorithmChooser';
import { AttributionPanel } from './AttributionPanel';
import { SaveBar } from './SaveBar';
import { GroupSaveResultBanner } from './GroupSaveResultBanner';
import { FieldValidationError } from './FieldValidationError';

interface GroupEditorFormProps {
  groupId: string | null; // null = /groups/new
  sensors: SensorEntry[];
}

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
  const hasErrors = !!memberFloorError || !!unitMismatchError || !!nameError;

  function toggleMember(entityId: string, checked: boolean) {
    draftMembers.value = checked
      ? [...draftMembers.value, entityId]
      : draftMembers.value.filter((id) => id !== entityId);
  }

  return (
    <div>
      <p class="argus-heading">{groupId ? 'Edit group' : 'Create group'}</p>

      <div class="argus-param-field">
        <label class="argus-param-field__label" for="group-name">
          Name
        </label>
        <input
          id="group-name"
          class="argus-param-field__input"
          type="text"
          value={draftFriendlyName.value}
          onInput={(e) => {
            const next = (e.target as HTMLInputElement).value;
            draftFriendlyName.value = next;
            if (!groupId) {
              draftGroupId.value = slugify(next);
            }
          }}
        />
        <FieldValidationError message={nameError ?? undefined} />
      </div>

      <div class="argus-param-field">
        <label class="argus-param-field__label" for="group-mode">
          Mode
        </label>
        <select
          id="group-mode"
          class="argus-param-field__input"
          value={draftMode.value}
          onChange={(e) => {
            draftMode.value = (e.target as HTMLSelectElement).value as typeof draftMode.value;
          }}
        >
          <option value="peer_divergence">Peer-divergence</option>
          <option value="joint">Joint (multivariate)</option>
        </select>
      </div>

      <p class="argus-heading">Members</p>
      <MemberPicker
        sensors={sensors}
        selectedIds={draftMembers.value}
        mode={draftMode.value}
        query={memberQuery}
        onQueryChange={setMemberQuery}
        onToggleMember={toggleMember}
      />

      <p class="argus-heading">Choose algorithm</p>
      <div id="algorithm-chooser-slot">
        <AlgorithmChooser existingDetector={groupId ? draftDetector.value : null} />
      </div>
      <FieldValidationError message={noAlgorithmError ?? undefined} />

      {groupId && <AttributionPanel groupId={groupId} />}

      <SaveBar saving={saving} disabled={saving || hasErrors} onSave={saveGroup} />

      {result && <GroupSaveResultBanner result={result} memberCount={draftMembers.value.length} />}
    </div>
  );
}
