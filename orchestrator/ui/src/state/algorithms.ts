// Algorithms screen data module (ALGO-07/08) — read-only detector catalog fetch.
// Mirrors state/groups.ts's signal + async-loader convention. Deliberately independent
// from state/groupEditor.ts's `catalog` signal (which wraps the full DetectorCatalog for
// the in-flow AlgorithmChooser wizard, guided answers included) — this screen only needs
// the `detectors` list, read-only, and never touches the `guided` field (that belongs to
// the wizard, D-05).

import { signal } from '@preact/signals';
import { apiGet } from '../api/client';
import type { DetectorCatalog, DetectorCatalogEntry } from '../api/types';

export const catalog = signal<DetectorCatalogEntry[]>([]);
export const loadError = signal(false);

/** Loads the detector catalog (GET api/detectors/catalog), preserving server order. */
export async function loadCatalog(): Promise<void> {
  try {
    const res = await apiGet<DetectorCatalog>('api/detectors/catalog');
    catalog.value = res.detectors;
    loadError.value = false;
  } catch {
    loadError.value = true;
  }
}
