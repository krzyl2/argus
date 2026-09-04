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
  // writes the same block for any entity it had to default. Validated key-by-key, those nine
  // absent keys were nine MSG_REQUIRED errors, and validationErrors aggregates across ALL
  // tracked entities -- so one such entity disabled Save for the entire screen, on a brand-new
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

    expect(validationErrors.value).toEqual({});
    expect(hasValidationErrors.value).toBe(false);
  });

  // The same failure, on the path where the fix cannot be papered over by hydration: when
  // GET /api/detectors/defaults fails, loadDetectorDefaults swallows it into "not loaded" and
  // defaultsFor returns {}, so nothing can fill an omitted key in. Save must still be reachable
  // -- the rule is "an omitted key is a default", not "an omitted key gets filled in".
  it('an entity saved with empty params does not block Save when the defaults table failed to load', async () => {
    resetDetectorDefaults(); // no seedDefaults(): this is the degraded path
    vi.spyOn(client, 'apiGet').mockImplementation((path: string) => {
      if (path.startsWith('api/detectors/defaults')) return Promise.reject(new Error('502'));
      return Promise.resolve({ entries: [entry({ detectors: [{ name: 'rmad', params: {} }] })] });
    });

    await loadSensors('');

    expect(validationErrors.value).toEqual({});
    expect(hasValidationErrors.value).toBe(false);
  });

  // The OTHER half of "validate the EFFECTIVE params": merging the defaults in is not only a
  // way to stop reporting absent keys, it is what makes a PARTIAL block checkable at all. The
  // cross-field rules (min_samples <= window, high > low) compare two keys, and a block that
  // stores one of the pair leaves the other to the default -- so validating the raw stored map
  // silently skips the comparison and the browser calls a configuration valid that the server
  // then rejects. These pin the merge itself: drop it and validationErrors goes empty.
  it('flags a partial block whose stored key is illegal AGAINST the default it is compared to', async () => {
    // window omitted -> default 720; min_samples 900 can never be reached inside it, so the
    // entity would sit in "calibrating" forever and never alarm.
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [entry({ detectors: [{ name: 'rmad', params: { min_samples: '900' } }] })],
    });

    await loadSensors('');

    expect(validationErrors.value['sensor.load_5m'][0]).toEqual({
      min_samples: 'Must not be greater than window.',
    });
    expect(hasValidationErrors.value).toBe(true);
  });

  it('flags a partial block whose stored threshold crosses the default on the other side', async () => {
    // high_threshold omitted -> default 0.5; a stored low of 0.9 sits above it, which would
    // mean an alarm band that can never close.
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [entry({ detectors: [{ name: 'rmad', params: { low_threshold: '0.9' } }] })],
    });

    await loadSensors('');

    expect(validationErrors.value['sensor.load_5m'][0]).toEqual({
      high_threshold: 'Must be between 0 and 1, and greater than low threshold.',
      low_threshold: 'Must be between 0 and 1, and less than high threshold.',
    });
  });

  // Hydration must not turn into a rewrite in EITHER direction: a stored block round-trips
  // key-for-key. Filling the omissions in would make the next Save materialize today's whole
  // default table onto disk, which pins the entity to today's numbers -- a later change to
  // DetectorDefaults would never reach it. `params: {}` means "use the defaults, including
  // future ones", and that meaning has to survive a Save.
  it('round-trips a partial saved block without materializing the defaults into it', async () => {
    const stored = { window: '240', high_threshold: '0.615' };
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [entry({ detectors: [{ name: 'rmad', params: stored }] })],
    });
    const postSpy = vi
      .spyOn(client, 'apiPost')
      .mockResolvedValue({ ok: true, count: 1, hasStreaming: true });

    await loadSensors('');

    expect(entityEdits.value['sensor.load_5m'].detectors[0].params).toEqual(stored);
    expect(hasValidationErrors.value).toBe(false);

    await save();
    const body = postSpy.mock.calls[0][1] as {
      entities: { detectors: { params: Record<string, string> }[] }[];
    };
    expect(body.entities[0].detectors[0].params).toEqual(stored);
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
