import { useEffect, useState } from 'preact/hooks';
import { sensors, loadSensors, setTracked } from '../state/sensors';
import { pendingPrefillMembers } from '../state/groups';
import { MemberPicker } from './MemberPicker';
import { Button } from './Button';

// D-06: thin hand-off — owns only sensor multi-select + the 1-vs->=2 branch. Never
// calls save() itself and never mounts the group AlgorithmChooser; both exits reuse
// the existing, unchanged save paths (GroupEditorForm via pendingPrefillMembers, or
// SingleDetectorEditorForm via setTracked).
export function AddDetectorWizard() {
  useEffect(() => {
    // D-07 (Pitfall 1, CRITICAL): load the FULL sensor set on mount, never rely on the
    // >=3-char search results for this — a later setTracked+save from a partially
    // loaded entityEdits would silently untrack every other sensor in entities.yaml.
    loadSensors('');
  }, []);

  const [query, setQuery] = useState('');
  const [selectedIds, setSelectedIds] = useState<string[]>([]);

  function toggleMember(entityId: string, checked: boolean) {
    setSelectedIds((prev) =>
      checked ? [...prev, entityId] : prev.filter((id) => id !== entityId)
    );
  }

  function handleContinue() {
    if (selectedIds.length >= 2) {
      // WIZ-03: hand off to the existing, unchanged /groups/new draft pre-fill channel —
      // zero receiving-end code, mirrors AreaSuggestionBanner's "Review" action exactly.
      pendingPrefillMembers.value = selectedIds;
      location.hash = '#/groups/new';
    } else if (selectedIds.length === 1) {
      // WIZ-02: track then hand off to the single-sensor editor. setTracked runs AFTER
      // the mount-time loadSensors('') above has hydrated the full entityEdits set (D-07).
      setTracked(selectedIds[0], true);
      location.hash = `#/detectors/sensor/${encodeURIComponent(selectedIds[0])}`;
    }
  }

  const buttonLabel = selectedIds.length >= 2 ? 'Create group' : 'Configure detector';

  return (
    <div>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">Add detector</h1>
        <p class="argus-page-header__subtitle">
          Select one sensor to configure its detector, or two or more to create a group.
        </p>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => {
            location.hash = '#/detectors';
          }}
        >
          Back to detectors
        </Button>
      </header>

      <MemberPicker
        sensors={sensors.value}
        selectedIds={selectedIds}
        mode="peer_divergence"
        query={query}
        onQueryChange={setQuery}
        onToggleMember={toggleMember}
        minQueryLength={3}
        showGroupValidation={false}
      />

      <p class="argus-label">
        {selectedIds.length} sensor{selectedIds.length === 1 ? '' : 's'} selected
      </p>

      <Button disabled={selectedIds.length === 0} onClick={handleContinue}>
        {buttonLabel}
      </Button>
    </div>
  );
}
