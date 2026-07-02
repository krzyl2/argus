import { signal, computed } from '@preact/signals';
import { apiGet, apiPost } from '../api/client';
import type { SensorEntry, SaveRequest, SaveResponse, DetectorEntry as ApiDetectorEntry } from '../api/types';
import { validateDetectorParams, hasAnyError } from '../validation/detectorParams';

// Detector default values — must match EntityPickerPage.cs constants exactly
// (07-UI-SPEC.md "Detector default values"). AddDetectorButton constructs new
// entries from this table client-side — no server round-trip needed.
export const DETECTOR_DEFAULTS: Record<'hst' | 'mad' | 'stl', Record<string, string>> = {
  hst: {
    window: '250',
    n_trees: '25',
    high_threshold: '0.7',
    low_threshold: '0.3',
    min_consecutive: '3',
    frozen_window: '10',
    frozen_variance_threshold: '0.001',
  },
  mad: {
    threshold: '3.5',
    window: '20',
  },
  stl: {
    period: '24',
    seasonal: '7',
    threshold: '3.0',
  },
};

export function makeDetectorEntry(name: 'hst' | 'mad' | 'stl'): ApiDetectorEntry {
  return { name, params: { ...DETECTOR_DEFAULTS[name] } };
}

export type SaveState = 'idle' | 'saving' | { result: SaveResponse };

// Editable in-memory model: entity id -> tracked + detector list.
export interface EntityEditState {
  isTracked: boolean;
  detectors: ApiDetectorEntry[];
}

export const query = signal('');
export const sensors = signal<SensorEntry[]>([]);
export const loading = signal(false);
export const entityEdits = signal<Record<string, EntityEditState>>({});
export const includePatterns = signal('');
export const excludePatterns = signal('');
export const saveState = signal<SaveState>('idle');

function getOrInitEdit(entityId: string, isTracked: boolean): EntityEditState {
  const existing = entityEdits.value[entityId];
  if (existing) return existing;
  return {
    isTracked,
    detectors: isTracked ? [makeDetectorEntry('hst')] : [],
  };
}

export async function loadSensors(q: string): Promise<void> {
  loading.value = true;
  try {
    const res = await apiGet<{ entries: SensorEntry[] }>(`api/sensors?q=${encodeURIComponent(q)}`);
    sensors.value = res.entries;
    const edits = { ...entityEdits.value };
    for (const entry of res.entries) {
      if (!edits[entry.entityId]) {
        edits[entry.entityId] = getOrInitEdit(entry.entityId, entry.isTracked);
      }
    }
    entityEdits.value = edits;
  } finally {
    loading.value = false;
  }
}

export function setTracked(entityId: string, tracked: boolean): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId] ?? { isTracked: false, detectors: [] };
  edits[entityId] = {
    isTracked: tracked,
    detectors: tracked && current.detectors.length === 0 ? [makeDetectorEntry('hst')] : current.detectors,
  };
  entityEdits.value = edits;
}

export function addDetector(entityId: string): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId] ?? { isTracked: true, detectors: [] };
  edits[entityId] = {
    ...current,
    detectors: [...current.detectors, makeDetectorEntry('hst')],
  };
  entityEdits.value = edits;
}

export function removeDetector(entityId: string, index: number): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId];
  if (!current) return;
  edits[entityId] = {
    ...current,
    detectors: current.detectors.filter((_, i) => i !== index),
  };
  entityEdits.value = edits;
}

export function updateDetectorName(entityId: string, index: number, name: 'hst' | 'mad' | 'stl'): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId];
  if (!current) return;
  const detectors = current.detectors.map((d, i) => (i === index ? makeDetectorEntry(name) : d));
  edits[entityId] = { ...current, detectors };
  entityEdits.value = edits;
}

export function updateDetectorParam(
  entityId: string,
  index: number,
  key: string,
  value: string
): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId];
  if (!current) return;
  const detectors = current.detectors.map((d, i) =>
    i === index ? { ...d, params: { ...d.params, [key]: value } } : d
  );
  edits[entityId] = { ...current, detectors };
  entityEdits.value = edits;
}

// Aggregate validation-error map across all tracked entities' detectors.
// Save button is disabled whenever this is non-empty (parity with usb()).
export const validationErrors = computed(() => {
  const allErrors: Record<string, Record<number, Record<string, string>>> = {};
  for (const [entityId, edit] of Object.entries(entityEdits.value)) {
    if (!edit.isTracked) continue;
    edit.detectors.forEach((det, idx) => {
      const errors = validateDetectorParams(det.name, det.params);
      if (hasAnyError(errors)) {
        allErrors[entityId] = allErrors[entityId] ?? {};
        allErrors[entityId][idx] = errors;
      }
    });
  }
  return allErrors;
});

export const hasValidationErrors = computed(() => Object.keys(validationErrors.value).length > 0);

export async function save(): Promise<void> {
  saveState.value = 'saving';
  const body: SaveRequest = {
    entities: Object.entries(entityEdits.value)
      .filter(([, edit]) => edit.isTracked)
      .map(([entityId, edit]) => ({ entityId, detectors: edit.detectors })),
    include: includePatterns.value,
    exclude: excludePatterns.value,
  };
  try {
    const result = await apiPost<SaveResponse>('api/sensors/save', body);
    saveState.value = { result };
  } catch (err) {
    saveState.value = {
      result: { ok: false, kind: 'error', reason: err instanceof Error ? err.message : 'unexpected error' },
    };
  }
}
