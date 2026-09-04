import { signal, computed } from '@preact/signals';
import { apiGet, apiPost } from '../api/client';
import type {
  SensorEntry,
  SaveRequest,
  SaveResponse,
  DetectorName,
  DetectorEntry as ApiDetectorEntry,
} from '../api/types';
import { validateDetectorParams, hasAnyError } from '../validation/detectorParams';
import { defaultsFor, defaultsLoaded, loadDetectorDefaults } from './detectorDefaults';

/**
 * Builds a NEW detector entry from the server's default table.
 *
 * WR-02 withdrawn: the client no longer carries its own copy of the numbers. If the table has
 * not arrived, this returns an entry with EMPTY params rather than invented ones — an empty
 * params map is exactly what "use all defaults" means to the server, whereas a guessed table
 * that drifted from DetectorDefaults.cs would be written to disk as though the operator had
 * chosen it. Call sites that MINT entries are additionally gated on defaultsLoaded.
 */
export function makeDetectorEntry(name: DetectorName): ApiDetectorEntry {
  return { name, params: defaultsFor(name) };
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

/**
 * Seeds the editor for one entity, hydrating from the server's SAVED detector list (D-N).
 *
 * This is the fix for a silent revert, not a convenience. `save()` replaces the entire
 * `entities:` list, so whatever sits in entityEdits when ANY screen saves — including the
 * pattern textareas in Settings — is what lands on disk for EVERY tracked sensor. Seeding
 * `[makeDetectorEntry('rmad')]` unconditionally therefore rewrote every operator-tuned block
 * (and, after the migration, every migrated block) with defaults on the first save.
 *
 * The default-entry fallback only applies to a tracked entity the server sent no detectors
 * for — a genuinely new selection.
 */
function getOrInitEdit(entityId: string, entry?: SensorEntry): EntityEditState {
  const existing = entityEdits.value[entityId];
  if (existing) return existing;

  const isTracked = entry?.isTracked ?? false;
  const saved = entry?.detectors;
  if (saved && saved.length > 0) {
    return {
      isTracked,
      // Saved params are layered ON TOP of the server default table, never used raw. A stored
      // block may legitimately omit keys: `params: {}` is what gen-entities.py writes on a fresh
      // install and what the server writes for an entity it defaulted, and an omitted key IS the
      // default on the server side (RmadParams.From). Rendered raw, every omitted key became an
      // empty field, validateDetectorParams reported MSG_REQUIRED on it, and because
      // validationErrors aggregates across ALL tracked entities a single such entity disabled
      // Save for the whole screen -- i.e. a fresh install could not save anything.
      //
      // This does not weaken D-N: the spread order puts every key the operator actually tuned
      // over the default, so the read-back still shows what is really on disk wherever disk
      // says anything at all.
      detectors: saved.map((d) => ({ name: d.name, params: { ...defaultsFor(d.name), ...d.params } })),
    };
  }
  return {
    isTracked,
    detectors: isTracked ? [makeDetectorEntry('rmad')] : [],
  };
}

// Monotonic request sequence — guards against out-of-order/racing loadSensors
// responses (e.g. rapid filter changes) overwriting newer state with a stale one.
let loadSensorsSeq = 0;

export async function loadSensors(q: string): Promise<void> {
  const seq = ++loadSensorsSeq;
  loading.value = true;
  try {
    // The defaults table is fetched alongside the sensor list, not lazily on first use: a
    // detector entry minted before it arrives would carry empty params.
    const [res] = await Promise.all([
      apiGet<{ entries: SensorEntry[] }>(`api/sensors?q=${encodeURIComponent(q)}`),
      loadDetectorDefaults(),
    ]);
    if (seq !== loadSensorsSeq) return; // stale response — a newer request is in flight/done
    sensors.value = res.entries;
    const edits = { ...entityEdits.value };
    for (const entry of res.entries) {
      if (!edits[entry.entityId]) {
        edits[entry.entityId] = getOrInitEdit(entry.entityId, entry);
      }
    }
    entityEdits.value = edits;
  } finally {
    if (seq === loadSensorsSeq) loading.value = false;
  }
}

export function setTracked(entityId: string, tracked: boolean): void {
  const edits = { ...entityEdits.value };
  const current = edits[entityId] ?? { isTracked: false, detectors: [] };
  edits[entityId] = {
    isTracked: tracked,
    detectors: tracked && current.detectors.length === 0 ? [makeDetectorEntry('rmad')] : current.detectors,
  };
  entityEdits.value = edits;
}

export function addDetector(entityId: string): void {
  // Gated on the server table: adding a detector MINTS a params block that the next save
  // writes to disk, so doing it before the defaults arrive would persist an empty block.
  if (!defaultsLoaded.value) return;
  const edits = { ...entityEdits.value };
  const current = edits[entityId] ?? { isTracked: true, detectors: [] };
  edits[entityId] = {
    ...current,
    detectors: [...current.detectors, makeDetectorEntry('rmad')],
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

export function updateDetectorName(entityId: string, index: number, name: DetectorName): void {
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
