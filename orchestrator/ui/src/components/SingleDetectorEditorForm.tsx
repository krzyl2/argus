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
import { SensorPresetPicker } from './SensorPresetPicker';
import { CalibratedBandReadout } from './CalibratedBandReadout';
import { ReplayPanel } from './ReplayPanel';
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

  // Everything the param help lines need to describe THIS sensor. z_scale comes from the
  // entity's own params so the "= odchylenie N sigma" line stays true if it is ever tuned.
  const streaming = detectors.find((d) => d.name === 'rmad');
  const ctx = {
    medianIntervalSec: entry?.medianIntervalSec ?? null,
    zScale: Number(streaming?.params.z_scale ?? '5') || 5,
    unitOfMeasurement: entry?.unitOfMeasurement ?? null,
  };
  const streamingIdx = detectors.findIndex((d) => d.name === 'rmad');

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

      {edit?.isTracked ? (
        <>
          {/* The band answers "is 0.5 right for THIS sensor?" — the number itself cannot. */}
          <CalibratedBandReadout entry={entry} />
          {streamingIdx >= 0 && (
            <SensorPresetPicker
              params={detectors[streamingIdx].params}
              onApply={(preset) => {
                // A preset writes ONLY its own keys; window/min_samples/scale_floor are in
                // units this sensor owns and must never move from a sensitivity radio button.
                for (const [key, value] of Object.entries(preset)) {
                  updateDetectorParam(entityId, streamingIdx, key, value);
                }
              }}
            />
          )}
          <DetectorDisclosure
          entityId={entityId}
          entityIdx={0}
          entityLabel={entityId}
          detectors={detectors}
          onTypeChange={(detIdx, name) => updateDetectorName(entityId, detIdx, name)}
          onParamChange={(detIdx, key, value) => updateDetectorParam(entityId, detIdx, key, value)}
          onRemove={(detIdx) => removeDetector(entityId, detIdx)}
          onAdd={() => addDetector(entityId)}
          ctx={ctx}
          />
          {/* WS6: replay the entity's own history through these EXACT params before saving
              them. Bound to the first detector block, which is the streaming one. */}
          {detectors.length > 0 && (
            <ReplayPanel
              entityId={entityId}
              detector={detectors[0].name}
              params={detectors[0].params}
            />
          )}
        </>
      ) : (
        <p class="argus-label">This sensor will be untracked on next save.</p>
      )}

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
