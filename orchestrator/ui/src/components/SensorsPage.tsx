import { useEffect } from 'preact/hooks';
import {
  query,
  sensors,
  entityEdits,
  includePatterns,
  excludePatterns,
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
import { SensorSearchInput } from './SensorSearchInput';
import { SensorList } from './SensorList';
import { PatternFiltersPanel } from './PatternFiltersPanel';
import { SaveBar } from './SaveBar';
import { SaveResultBanner } from './SaveResultBanner';

// Single-route entity picker + detector assignment + save (replaces the v3.0
// full-page BuildFullPage rendering).
export function SensorsPage() {
  useEffect(() => {
    loadSensors(query.value);
  }, []);

  function handleSearchChange(next: string) {
    query.value = next;
    loadSensors(next);
  }

  const saving = saveState.value === 'saving';
  const result = typeof saveState.value === 'object' ? saveState.value.result : null;

  return (
    <div>
      <div>
        <p class="argus-heading">Entity Selection</p>
        <p class="argus-body">Select the sensors Argus monitors and assign detectors to each.</p>
      </div>

      <SensorSearchInput value={query.value} onChange={handleSearchChange} />

      <p class="argus-heading">Sensors</p>
      <SensorList
        entries={sensors.value}
        query={query.value}
        edits={entityEdits.value}
        onToggleTracked={setTracked}
        onDetectorTypeChange={updateDetectorName}
        onDetectorParamChange={updateDetectorParam}
        onDetectorRemove={removeDetector}
        onDetectorAdd={addDetector}
      />

      <p class="argus-heading">Pattern Filters</p>
      <PatternFiltersPanel
        include={includePatterns.value}
        exclude={excludePatterns.value}
        onIncludeChange={(v) => (includePatterns.value = v)}
        onExcludeChange={(v) => (excludePatterns.value = v)}
      />

      <SaveBar saving={saving} disabled={saving || hasValidationErrors.value} onSave={save} />

      {result && <SaveResultBanner result={result} />}
    </div>
  );
}
