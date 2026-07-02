import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { loadSensors, sensors, loading } from './sensors';
import * as client from '../api/client';

describe('loadSensors', () => {
  beforeEach(() => {
    sensors.value = [];
    loading.value = false;
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
