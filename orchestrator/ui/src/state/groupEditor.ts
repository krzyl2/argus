// Guided-flow algorithm chooser state machine (ALGO-01..04) — mirrors state/sensors.ts's
// signal + pure-function-mutator style. See 08-RESEARCH.md Pattern 4 (state machine) and
// Pattern 3 (catalog shape). Copied close to verbatim from RESEARCH's draft, extended to
// load the catalog once and derive the guided answer->detector map from it (not hardcoded).

import { signal } from '@preact/signals';
import { apiGet } from '../api/client';
import type { DetectorCatalog, GroupDetectorName } from '../api/types';

export type ChooserMode = 'guided-question' | 'guided-pick-shown' | 'manual';

export const chooserMode = signal<ChooserMode>('guided-question');
export const selectedDetector = signal<GroupDetectorName | null>(null);
// Non-null only while showing "Suggested based on your answer" — cleared the instant the
// operator overrides via pickAlgorithmManually (UI-SPEC Guided Flow Contract #3).
export const guidedRecommended = signal<GroupDetectorName | null>(null);

export const catalog = signal<DetectorCatalog | null>(null);
export const catalogLoading = signal(false);

let loadCatalogSeq = 0;

/** Loads the detector catalog once (GET api/detectors/catalog) into the catalog signal. */
export async function loadCatalog(): Promise<void> {
  const seq = ++loadCatalogSeq;
  catalogLoading.value = true;
  try {
    const res = await apiGet<DetectorCatalog>('api/detectors/catalog');
    if (seq !== loadCatalogSeq) return; // stale response guard, same pattern as loadSensors/loadGroups
    catalog.value = res;
  } finally {
    if (seq === loadCatalogSeq) catalogLoading.value = false;
  }
}

/**
 * Resets the chooser to its initial guided-question state (entering a fresh
 * group editor session). Does not touch the catalog signal (loaded once, reused).
 */
export function resetChooser(): void {
  chooserMode.value = 'guided-question';
  selectedDetector.value = null;
  guidedRecommended.value = null;
}

/**
 * Pre-selects a detector for an existing draft (entering /groups/:id with a
 * detector already saved) — skips the guided question entirely since the
 * operator already made a choice in a prior session.
 */
export function loadChooserFromDetector(detector: GroupDetectorName): void {
  chooserMode.value = 'manual';
  selectedDetector.value = detector;
  guidedRecommended.value = null;
}

/**
 * Answers the guided "What are you monitoring?" question. Looks up the
 * answer->detector mapping from the loaded catalog's guided block (ALGO-04) —
 * falls back to the RESEARCH-documented pair if the catalog has not loaded yet
 * (should not happen in practice since GroupEditorForm loads the catalog before
 * rendering the chooser, but keeps this function safe to call standalone/in tests).
 */
export function answerGuidedQuestion(answer: string): void {
  const fromCatalog = catalog.value?.guided.find((g) => g.answer === answer)?.detector;
  const detector = fromCatalog ?? (answer === 'together' ? 'ecod' : 'peer_divergence');
  guidedRecommended.value = detector;
  selectedDetector.value = detector;
  chooserMode.value = 'guided-pick-shown';
}

/** "Skip — choose manually" — clears any guided recommendation, shows the plain grid. */
export function skipToManual(): void {
  guidedRecommended.value = null;
  chooserMode.value = 'manual';
}

/**
 * One click on any AlgorithmCard (guided or manual mode). Overriding a guided
 * pick clears the "guided" label in the same synchronous update — zero friction,
 * no confirmation (UI-SPEC Guided Flow Contract #3).
 */
export function pickAlgorithmManually(detector: GroupDetectorName): void {
  guidedRecommended.value = null;
  selectedDetector.value = detector;
  chooserMode.value = 'manual';
}
