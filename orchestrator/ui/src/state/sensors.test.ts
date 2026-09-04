import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  loadSensors,
  sensors,
  loading,
  entityEdits,
  addDetector,
  save,
  validationErrors,
  hasValidationErrors,
} from './sensors';
import { detectorDefaults, resetDetectorDefaults } from './detectorDefaults';
import * as client from '../api/client';

// loadSensors now fetches the server default table alongside the sensor list. Seeding it here
// keeps that fetch from consuming one of the apiGet mocks these ordering tests depend on —
// loadDetectorDefaults short-circuits once the table is present.
const RMAD_TABLE: Record<string, string> = {
  window: '720',
  min_samples: '60',
  z_scale: '5.0',
  scale_floor: '0.0',
  high_threshold: '0.5',
  low_threshold: '0.375',
  min_consecutive: '3',
  frozen_window: '10',
  frozen_variance_threshold: '0.0',
};

function seedDefaults() {
  detectorDefaults.value = {
    rmad: {
      window: '720',
      min_samples: '60',
      z_scale: '5.0',
      scale_floor: '0.0',
      high_threshold: '0.5',
      low_threshold: '0.375',
      min_consecutive: '3',
      frozen_window: '10',
      frozen_variance_threshold: '0.0',
    },
  };
}

describe('loadSensors', () => {
  beforeEach(() => {
    sensors.value = [];
    loading.value = false;
    entityEdits.value = {};
    resetDetectorDefaults();
    seedDefaults();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('populates sensors on a single successful call', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [{ entityId: 'sensor.a', friendlyName: null, currentValue: '1', unitOfMeasurement: null, isTracked: false }],
    });

    await loadSensors('a');

    expect(sensors.value).toHaveLength(1);
    expect(sensors.value[0].entityId).toBe('sensor.a');
    expect(loading.value).toBe(false);
  });

  it('ignores a stale response that resolves after a newer request (out-of-order race)', async () => {
    // First call: slow, resolves AFTER the second call.
    let resolveFirst!: (v: { entries: typeof firstEntries }) => void;
    const firstEntries = [
      { entityId: 'sensor.stale', friendlyName: null, currentValue: '1', unitOfMeasurement: null, isTracked: false },
    ];
    const secondEntries = [
      { entityId: 'sensor.fresh', friendlyName: null, currentValue: '2', unitOfMeasurement: null, isTracked: false },
    ];

    const apiGetSpy = vi.spyOn(client, 'apiGet');
    apiGetSpy.mockImplementationOnce(
      () => new Promise((resolve) => { resolveFirst = resolve; })
    );
    apiGetSpy.mockImplementationOnce(() => Promise.resolve({ entries: secondEntries }));

    const firstCall = loadSensors('stale-query');
    const secondCall = loadSensors('fresh-query');

    // Second (newer) request resolves first.
    await secondCall;
    expect(sensors.value).toEqual(secondEntries);

    // Now let the first (older, stale) request resolve — it must NOT overwrite state.
    resolveFirst({ entries: firstEntries });
    await firstCall;

    expect(sensors.value).toEqual(secondEntries);
  });

  it('does not flip loading back to true->false incorrectly when a stale request finishes after a newer one', async () => {
    let resolveFirst!: (v: { entries: never[] }) => void;
    const apiGetSpy = vi.spyOn(client, 'apiGet');
    apiGetSpy.mockImplementationOnce(
      () => new Promise((resolve) => { resolveFirst = resolve; })
    );
    apiGetSpy.mockImplementationOnce(() => Promise.resolve({ entries: [] }));

    const firstCall = loadSensors('a');
    const secondCall = loadSensors('b');

    await secondCall;
    expect(loading.value).toBe(false);

    resolveFirst({ entries: [] });
    await firstCall;

    // Stale finally-block must not touch loading once a newer request has already
    // completed and reset it.
    expect(loading.value).toBe(false);
  });
});


describe('detector hydration (D-N)', () => {
  beforeEach(() => {
    sensors.value = [];
    loading.value = false;
    entityEdits.value = {};
    resetDetectorDefaults();
    seedDefaults();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function entry(overrides: Record<string, unknown> = {}) {
    return {
      entityId: 'sensor.load_5m',
      friendlyName: null,
      currentValue: '1',
      unitOfMeasurement: null,
      isTracked: true,
      areaName: null,
      domain: 'sensor',
      ...overrides,
    };
  }

  // This is the fix for a silent revert, not a convenience. save() replaces the ENTIRE
  // entities list, so whatever sits in entityEdits when ANY screen saves is what lands on
  // disk for EVERY tracked sensor. Seeding a fresh default block therefore rewrote every
  // operator-tuned block — and, after the migration, every migrated block — on the first save
  // from any screen, including the pattern textareas in Settings.
  it('getOrInitEdit_HydratesFromServerDetectors', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [
        entry({
          detectors: [
            {
              name: 'rmad',
              params: { ...RMAD_TABLE, window: '240', high_threshold: '0.615' },
            },
          ],
        }),
      ],
    });

    await loadSensors('');

    const edit = entityEdits.value['sensor.load_5m'];
    expect(edit.detectors).toHaveLength(1);
    expect(edit.detectors[0].name).toBe('rmad');
    expect(edit.detectors[0].params.window).toBe('240');
    // The tuned value, NOT the default — the seeded default would have been '0.5'.
    expect(edit.detectors[0].params.high_threshold).not.toBe('0.5');
    expect(edit.detectors[0].params.high_threshold).toBe('0.615');
  });

  it('Save_AfterPlainLoad_RoundTripsTunedParamsUnchanged', async () => {
    const tuned = { ...RMAD_TABLE, window: '240', high_threshold: '0.615', scale_floor: '0.3' };
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [entry({ detectors: [{ name: 'rmad', params: tuned }] })],
    });
    const postSpy = vi
      .spyOn(client, 'apiPost')
      .mockResolvedValue({ ok: true, count: 1, hasStreaming: true });

    // Load, touch nothing, save — the exact shape of "operator edited patterns in Settings".
    await loadSensors('');
    await save();

    const body = postSpy.mock.calls[0][1] as {
      entities: { entityId: string; detectors: { name: string; params: Record<string, string> }[] }[];
    };
    expect(body.entities).toHaveLength(1);
    expect(body.entities[0].detectors[0].params).toEqual(tuned);
  });

  // A fresh install writes `params: {}` for EVERY entity (gen-entities.py), and the server
  // writes the same block for any entity it had to default. Read back raw, those nine empty
  // fields are nine MSG_REQUIRED errors, and validationErrors aggregates across ALL tracked
  // entities -- so one such entity disabled Save for the entire screen, on a brand-new
  // install, with nothing visibly wrong on any field.
  it('an entity saved with empty params does not block Save for the whole screen', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [
        entry({ detectors: [{ name: 'rmad', params: {} }] }),
        entry({
          entityId: 'sensor.other',
          detectors: [{ name: 'rmad', params: { ...RMAD_TABLE } }],
        }),
      ],
    });

    await loadSensors('');

    // An omitted key IS the default on the server (RmadParams.From) -- so showing the default
    // is showing what is in force, not inventing a value.
    expect(entityEdits.value['sensor.load_5m'].detectors[0].params).toEqual(RMAD_TABLE);
    expect(validationErrors.value).toEqual({});
    expect(hasValidationErrors.value).toBe(false);
  });

  // The merge must not turn into a rewrite: a key the operator actually tuned still wins over
  // the default, or this fix would reintroduce the silent revert D-N exists to prevent.
  it('fills only the keys the saved block omits, never the ones it carries', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [
        entry({
          detectors: [{ name: 'rmad', params: { window: '240', high_threshold: '0.615' } }],
        }),
      ],
    });

    await loadSensors('');

    const params = entityEdits.value['sensor.load_5m'].detectors[0].params;
    expect(params.window).toBe('240');
    expect(params.high_threshold).toBe('0.615');
    expect(params.min_samples).toBe('60');
    expect(params.z_scale).toBe('5.0');
    expect(hasValidationErrors.value).toBe(false);
  });

  // A tracked entity the server sent no detectors for is a genuinely new selection, and rmad
  // is the default (D-A). hst scores rarity rather than deviation (F4), so nothing may ever
  // land on it by default.
  it('seeds rmad — never hst — for a tracked entity with no saved detectors', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ entries: [entry({ detectors: null })] });

    await loadSensors('');

    const edit = entityEdits.value['sensor.load_5m'];
    expect(edit.detectors).toHaveLength(1);
    expect(edit.detectors[0].name).toBe('rmad');
    expect(edit.detectors[0].params.window).toBe('720');
  });

  it('leaves an untracked entity with no detectors at all', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [entry({ isTracked: false, detectors: null })],
    });

    await loadSensors('');

    expect(entityEdits.value['sensor.load_5m'].detectors).toEqual([]);
  });

  // addDetector MINTS a params block that the next save writes to disk. Before the server
  // table arrives the client has no numbers of its own (WR-02 withdrawn), so the only honest
  // options are "do nothing" or "persist an empty block" — and the empty block is what the
  // operator would later find on disk.
  it('refuses to add a detector before the server defaults arrive', () => {
    resetDetectorDefaults();
    entityEdits.value = { 'sensor.load_5m': { isTracked: true, detectors: [] } };

    addDetector('sensor.load_5m');

    expect(entityEdits.value['sensor.load_5m'].detectors).toEqual([]);

    seedDefaults();
    addDetector('sensor.load_5m');
    expect(entityEdits.value['sensor.load_5m'].detectors).toHaveLength(1);
    expect(entityEdits.value['sensor.load_5m'].detectors[0].name).toBe('rmad');
  });
});
