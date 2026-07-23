import { describe, it, expect, beforeEach } from 'vitest';
import { detectorRows } from './detectors';
import { groups, groupStatuses } from './groups';
import { sensors, entityEdits } from './sensors';
import type { GroupConfig, GroupStatus } from '../api/types';
import type { SensorEntry } from '../api/types';

function makeGroup(groupId: string): GroupConfig {
  return {
    groupId,
    friendlyName: groupId,
    members: ['sensor.a', 'sensor.b'],
    mode: 'peer_divergence',
    detector: 'peer_divergence',
    params: {},
  };
}

function makeSensor(entityId: string, isTracked: boolean): SensorEntry {
  return {
    entityId,
    friendlyName: null,
    currentValue: '1',
    unitOfMeasurement: null,
    isTracked,
    areaName: null,
    domain: 'sensor',
  };
}

function makeStatus(overrides: Partial<GroupStatus> = {}): GroupStatus {
  return {
    groupId: 'group_1',
    score: 0.5,
    isAnomaly: false,
    detector: 'peer_divergence',
    scoredAtUtc: '2026-07-23T00:00:00Z',
    contributions: [],
    ...overrides,
  };
}

describe('detectorRows (D-03/DET-01 merge)', () => {
  beforeEach(() => {
    groups.value = [];
    sensors.value = [];
    entityEdits.value = {};
    groupStatuses.value = {};
  });

  it('merges groups + tracked-only sensors into 4 rows (2 groups + 2 of 3 tracked sensors)', () => {
    groups.value = [makeGroup('group_1'), makeGroup('group_2')];
    sensors.value = [
      makeSensor('sensor.tracked_a', true),
      makeSensor('sensor.tracked_b', true),
      makeSensor('sensor.untracked', false),
    ];
    entityEdits.value = {
      'sensor.tracked_a': { isTracked: true, detectors: [] },
      'sensor.tracked_b': { isTracked: true, detectors: [] },
      'sensor.untracked': { isTracked: false, detectors: [] },
    };

    expect(detectorRows.value).toHaveLength(4);
  });

  it('every group row has kind==="group" and a group:-prefixed key', () => {
    groups.value = [makeGroup('group_1')];
    sensors.value = [];

    const groupRows = detectorRows.value.filter((r) => r.kind === 'group');
    expect(groupRows).toHaveLength(1);
    expect(groupRows[0].key).toBe('group:group_1');
    expect(groupRows[0].group?.groupId).toBe('group_1');
  });

  it('every sensor row has kind==="sensor" and a sensor:-prefixed key', () => {
    groups.value = [];
    sensors.value = [makeSensor('sensor.tracked_a', true)];
    entityEdits.value = { 'sensor.tracked_a': { isTracked: true, detectors: [] } };

    const sensorRows = detectorRows.value.filter((r) => r.kind === 'sensor');
    expect(sensorRows).toHaveLength(1);
    expect(sensorRows[0].key).toBe('sensor:sensor.tracked_a');
    expect(sensorRows[0].entry?.entityId).toBe('sensor.tracked_a');
  });

  it('excludes an untracked sensor', () => {
    sensors.value = [makeSensor('sensor.untracked', false)];
    entityEdits.value = { 'sensor.untracked': { isTracked: false, detectors: [] } };

    expect(detectorRows.value).toHaveLength(0);
  });

  it('includes a sensor tracked only in entityEdits (not the server isTracked flag)', () => {
    sensors.value = [makeSensor('sensor.client_tracked', false)];
    entityEdits.value = { 'sensor.client_tracked': { isTracked: true, detectors: [] } };

    expect(detectorRows.value).toHaveLength(1);
    expect(detectorRows.value[0].key).toBe('sensor:sensor.client_tracked');
  });

  // Intent: a group row must carry the last-known status from groupStatuses so the
  // Detectors list can render its verdict badge; an unfetched group carries `undefined`.
  it('a group row status is undefined when no status has been fetched', () => {
    groups.value = [makeGroup('group_1')];

    const groupRow = detectorRows.value.find((r) => r.kind === 'group');
    expect(groupRow?.status).toBeUndefined();
  });

  it('a group row carries a null status (fetched but never scored)', () => {
    groups.value = [makeGroup('group_1')];
    groupStatuses.value = { group_1: null };

    const groupRow = detectorRows.value.find((r) => r.kind === 'group');
    expect(groupRow?.status).toBeNull();
  });

  it('a group row carries its scored GroupStatus (isAnomaly flag preserved)', () => {
    groups.value = [makeGroup('group_1')];
    groupStatuses.value = { group_1: makeStatus({ groupId: 'group_1', isAnomaly: true }) };

    const groupRow = detectorRows.value.find((r) => r.kind === 'group');
    expect(groupRow?.status?.isAnomaly).toBe(true);
  });
});
