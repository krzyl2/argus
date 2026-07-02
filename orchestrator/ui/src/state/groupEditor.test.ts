import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  chooserMode,
  selectedDetector,
  guidedRecommended,
  catalog,
  answerGuidedQuestion,
  skipToManual,
  pickAlgorithmManually,
  resetChooser,
  loadChooserFromDetector,
  loadCatalog,
} from './groupEditor';
import * as client from '../api/client';
import type { DetectorCatalog } from '../api/types';

function resetSignals() {
  chooserMode.value = 'guided-question';
  selectedDetector.value = null;
  guidedRecommended.value = null;
  catalog.value = null;
}

describe('answerGuidedQuestion', () => {
  beforeEach(resetSignals);

  it("'together' pre-selects ecod, labels it guided, and shows the pick", () => {
    answerGuidedQuestion('together');

    expect(selectedDetector.value).toBe('ecod');
    expect(guidedRecommended.value).toBe('ecod');
    expect(chooserMode.value).toBe('guided-pick-shown');
  });

  it("'diverges' pre-selects peer_divergence", () => {
    answerGuidedQuestion('diverges');

    expect(selectedDetector.value).toBe('peer_divergence');
    expect(guidedRecommended.value).toBe('peer_divergence');
    expect(chooserMode.value).toBe('guided-pick-shown');
  });

  it('uses the catalog guided map when loaded, over the hardcoded fallback', () => {
    catalog.value = {
      detectors: [],
      guided: [{ answer: 'together', detector: 'pca' }],
    } as unknown as DetectorCatalog;

    answerGuidedQuestion('together');

    expect(selectedDetector.value).toBe('pca');
  });
});

describe('pickAlgorithmManually', () => {
  beforeEach(resetSignals);

  it('overriding a guided pick clears the guided label in one synchronous update', () => {
    answerGuidedQuestion('together');
    expect(guidedRecommended.value).toBe('ecod');

    pickAlgorithmManually('iforest');

    expect(selectedDetector.value).toBe('iforest');
    expect(guidedRecommended.value).toBeNull();
    expect(chooserMode.value).toBe('manual');
  });

  it('picking a card with no prior guided answer just selects it manually', () => {
    pickAlgorithmManually('copod');

    expect(selectedDetector.value).toBe('copod');
    expect(guidedRecommended.value).toBeNull();
    expect(chooserMode.value).toBe('manual');
  });
});

describe('skipToManual', () => {
  beforeEach(resetSignals);

  it('clears any guided recommendation and switches to manual mode', () => {
    answerGuidedQuestion('together');

    skipToManual();

    expect(guidedRecommended.value).toBeNull();
    expect(chooserMode.value).toBe('manual');
    // Skip does not clear the underlying selection made by the guided answer.
  });

  it('is available directly from the question state (never forces the guided path)', () => {
    expect(chooserMode.value).toBe('guided-question');
    skipToManual();
    expect(chooserMode.value).toBe('manual');
  });
});

describe('resetChooser', () => {
  beforeEach(resetSignals);

  it('resets mode/selection/recommendation to the initial guided-question state', () => {
    answerGuidedQuestion('together');

    resetChooser();

    expect(chooserMode.value).toBe('guided-question');
    expect(selectedDetector.value).toBeNull();
    expect(guidedRecommended.value).toBeNull();
  });
});

describe('loadChooserFromDetector', () => {
  beforeEach(resetSignals);

  it('skips the guided question and pre-selects the given detector with no guided label', () => {
    loadChooserFromDetector('pca');

    expect(chooserMode.value).toBe('manual');
    expect(selectedDetector.value).toBe('pca');
    expect(guidedRecommended.value).toBeNull();
  });
});

describe('loadCatalog', () => {
  beforeEach(resetSignals);

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('populates the catalog signal on a successful call', async () => {
    const fakeCatalog: DetectorCatalog = {
      detectors: [
        { name: 'ecod', bestFor: 'joint anomalies', presets: [], paramSchema: [] },
      ],
      guided: [{ answer: 'together', detector: 'ecod' }],
    };
    vi.spyOn(client, 'apiGet').mockResolvedValue(fakeCatalog);

    await loadCatalog();

    expect(catalog.value).toEqual(fakeCatalog);
  });

  it('ignores a stale response that resolves after a newer request (out-of-order race)', async () => {
    let resolveFirst!: (v: DetectorCatalog) => void;
    const staleCatalog: DetectorCatalog = { detectors: [], guided: [] };
    const freshCatalog: DetectorCatalog = {
      detectors: [{ name: 'pca', bestFor: 'x', presets: [], paramSchema: [] }],
      guided: [],
    };

    const apiGetSpy = vi.spyOn(client, 'apiGet');
    apiGetSpy.mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve; }));
    apiGetSpy.mockImplementationOnce(() => Promise.resolve(freshCatalog));

    const firstCall = loadCatalog();
    const secondCall = loadCatalog();

    await secondCall;
    expect(catalog.value).toEqual(freshCatalog);

    resolveFirst(staleCatalog);
    await firstCall;

    expect(catalog.value).toEqual(freshCatalog);
  });
});
