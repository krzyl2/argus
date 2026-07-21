import { useEffect } from 'preact/hooks';
import {
  sensors,
  entityEdits,
  saveState,
  hasValidationErrors,
  loadSensors,
  setTracked,
  addDetector,
  removeDetector,
  updateDetectorName,
  updateDetectorParam,
  save,
} from '../state/sensors';
import { DetectorDisclosure } from './DetectorDisclosure';
import { SaveBar } from './SaveBar';
import { SaveResultBanner } from './SaveResultBanner';
import { Button } from './Button';

interface SingleDetectorEditorFormProps {
  entityId: string;
}

// D-05: extracted from SensorsPage's inline detector-assignment block, scoped to a
// single entityId. This form imports ONLY from state/sensors — never state/groups or
// state/groupEditor, and never mounts AlgorithmChooser (Pitfall 6: that component's
// single-sync-point effect writes into the *group* draft; mounting it here would
// cross-contaminate any group draft the operator has open elsewhere).
export function SingleDetectorEditorForm({ entityId }: SingleDetectorEditorFormProps) {
  useEffect(() => {
    // D-07 (Pitfall 1, CRITICAL): load the FULL sensor set, not a filtered one — this
    // route never shows a search box, and save() is a full-list-replace of `entities:`.
    // A partial load here would silently untrack every other sensor on the next save.
    loadSensors('');
  }, []);

  const entry = sensors.value.find((s) => s.entityId === entityId);
  const title = entry?.friendlyName || entityId;
  const edit = entityEdits.value[entityId];
  const detectors = edit?.detectors ?? [];

  const saving = saveState.value === 'saving';
  const result = typeof saveState.value === 'object' ? saveState.value.result : null;

  return (
    <div>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">{title}</h1>
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

      <DetectorDisclosure
        entityId={entityId}
        entityIdx={0}
        detectors={detectors}
        onTypeChange={(detIdx, name) => updateDetectorName(entityId, detIdx, name)}
        onParamChange={(detIdx, key, value) => updateDetectorParam(entityId, detIdx, key, value)}
        onRemove={(detIdx) => removeDetector(entityId, detIdx)}
        onAdd={() => addDetector(entityId)}
      />

      <Button
        variant="destructive-ghost"
        size="xs"
        onClick={() => setTracked(entityId, false)}
      >
        Untrack sensor
      </Button>

      <SaveBar saving={saving} disabled={saving || hasValidationErrors.value} onSave={save} />

      {result && <SaveResultBanner result={result} />}
    </div>
  );
}
