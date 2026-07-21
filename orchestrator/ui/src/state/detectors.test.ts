import { describe, it, expect, beforeEach } from 'vitest';
import { detectorRows } from './detectors';
import { groups } from './groups';
import { sensors, entityEdits } from './sensors';
import type { GroupConfig } from '../api/types';
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

describe('detectorRows (D-03/DET-01 merge)', () => {
  beforeEach(() => {
    groups.value = [];
    sensors.value = [];
    entityEdits.value = {};
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
});
