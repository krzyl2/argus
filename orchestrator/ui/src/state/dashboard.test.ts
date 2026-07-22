import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { loadDashboard, trackedCount, groupCount, loadError, health, recentAnomalies } from './dashboard';
import * as client from '../api/client';

const sensorsFixture = {
  entries: [
    { entityId: 'sensor.a', friendlyName: null, currentValue: '1', unitOfMeasurement: null, isTracked: true, areaName: null, domain: 'sensor' },
    { entityId: 'sensor.b', friendlyName: null, currentValue: '2', unitOfMeasurement: null, isTracked: false, areaName: null, domain: 'sensor' },
  ],
};

const groupsFixture = {
  groups: [
    { groupId: 'g1', friendlyName: 'Group 1', members: ['sensor.a', 'sensor.b'], mode: 'joint', detector: 'ecod', params: {} },
  ],
};

const healthFixture = {
  homeAssistant: { connected: true, entityCount: 42 },
  components: [
    { key: 'homeAssistant', label: 'Home Assistant (WebSocket)', status: 'ok', detail: 'Connected · 42 entities' },
  ],
};

const anomaliesFixture = {
  anomalies: [
    { entityId: 'sensor.a', groupId: null, score: 0.9, detector: 'hst', detectedAtUtc: '2026-07-22T12:00:00Z' },
  ],
};

function mockApiGetByPath(overrides: Partial<Record<string, unknown>> = {}, rejectPaths: string[] = []) {
  return vi.spyOn(client, 'apiGet').mockImplementation((path: string) => {
    if (rejectPaths.includes(path)) {
      return Promise.reject(new Error(`GET ${path} failed`));
    }
    const fixtures: Record<string, unknown> = {
      'api/sensors': sensorsFixture,
      'api/groups': groupsFixture,
      'api/health': healthFixture,
      'api/anomalies/recent': anomaliesFixture,
      ...overrides,
    };
    return Promise.resolve(fixtures[path]);
  });
}

describe('loadDashboard', () => {
  beforeEach(() => {
    trackedCount.value = null;
    groupCount.value = null;
    loadError.value = false;
    health.value = null;
    recentAnomalies.value = null;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('populates counts, health, and recentAnomalies on a fully-successful load', async () => {
    mockApiGetByPath();

    await loadDashboard();

    expect(trackedCount.value).toBe(1);
    expect(groupCount.value).toBe(1);
    expect(loadError.value).toBe(false);
    expect(health.value).toEqual(healthFixture);
    expect(recentAnomalies.value).toEqual(anomaliesFixture.anomalies);
  });

  it('degrades health independently — a failing api/health call leaves counts and anomalies populated', async () => {
    mockApiGetByPath({}, ['api/health']);

    await loadDashboard();

    expect(health.value).toBeNull();
    expect(trackedCount.value).toBe(1);
    expect(groupCount.value).toBe(1);
    expect(recentAnomalies.value).toEqual(anomaliesFixture.anomalies);
    expect(loadError.value).toBe(false);
  });

  it('decouples counts failure from health — a failing api/sensors call sets loadError but health still loads', async () => {
    mockApiGetByPath({}, ['api/sensors']);

    await loadDashboard();

    expect(loadError.value).toBe(true);
    expect(trackedCount.value).toBeNull();
    expect(groupCount.value).toBeNull();
    expect(health.value).toEqual(healthFixture);
  });
});
