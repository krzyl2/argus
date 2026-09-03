import { signal, computed } from '@preact/signals';
import { apiGet } from '../api/client';
import type { DetectorDefaultsResponse, DetectorName, DetectorPreset } from '../api/types';

/**
 * Server-owned detector default tables and sensitivity presets.
 *
 * WR-02 is WITHDRAWN. The client used to keep its own copy of the default numbers
 * (DETECTOR_DEFAULTS in state/sensors.ts) so it could build a new detector entry without a
 * round-trip. That made two tables that had to be edited together, and after the rmad
 * migration a stale client copy would not be a cosmetic drift — it would write hst-shaped
 * params over a migrated entity on the next save. So the numbers now live in exactly one
 * place (DetectorDefaults.cs) and the client fetches them.
 */
export const detectorDefaults = signal<Record<string, Record<string, string>>>({});
export const detectorPresets = signal<DetectorPreset[] | null>(null);

/** True once the server table has arrived. Gates anything that would MINT a detector entry. */
export const defaultsLoaded = computed(() => Object.keys(detectorDefaults.value).length > 0);

let inFlight: Promise<void> | null = null;

/**
 * Loads the table once per session. Concurrent callers share the same request — loadSensors
 * runs this in a Promise.all and several screens call loadSensors on mount.
 *
 * A failure is deliberately swallowed into "not loaded": the screens degrade to read-only
 * (addDetector is disabled while defaultsLoaded is false) rather than minting entries from
 * numbers the client invented.
 */
export function loadDetectorDefaults(): Promise<void> {
  if (defaultsLoaded.value) return Promise.resolve();
  if (inFlight) return inFlight;

  inFlight = apiGet<DetectorDefaultsResponse>('api/detectors/defaults')
    .then((res) => {
      detectorDefaults.value = res.defaults ?? {};
      detectorPresets.value = res.presets?.rmad ?? null;
    })
    .catch(() => {
      detectorDefaults.value = {};
      detectorPresets.value = null;
    })
    .finally(() => {
      inFlight = null;
    });

  return inFlight;
}

/** The server's default params for one detector, or an empty map when not loaded yet. */
export function defaultsFor(name: DetectorName): Record<string, string> {
  return { ...(detectorDefaults.value[name] ?? {}) };
}

/** Test seam: drops the cached table so a test can re-stub the endpoint. */
export function resetDetectorDefaults(): void {
  detectorDefaults.value = {};
  detectorPresets.value = null;
  inFlight = null;
}
