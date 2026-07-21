import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { DetectorList } from './DetectorList';
import type { DetectorRow } from '../state/detectors';
import type { GroupConfig, SensorEntry } from '../api/types';

function makeGroup(overrides: Partial<GroupConfig> = {}): GroupConfig {
  return {
    groupId: 'living_room',
    friendlyName: 'Living Room',
    members: ['sensor.a', 'sensor.b'],
    mode: 'peer_divergence',
    detector: 'peer_divergence',
    params: {},
    ...overrides,
  };
}

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.living_room_temp',
    friendlyName: null,
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('DetectorList', () => {
  it('wraps rows in a Card', () => {
    const rows: DetectorRow[] = [{ key: 'group:living_room', kind: 'group', group: makeGroup() }];
    const { container } = render(<DetectorList rows={rows} />);
    expect(container.querySelector('.argus-card ul.argus-list')).not.toBeNull();
  });

  it('renders one row per entry across the unified group + sensor list (DET-01)', () => {
    const rows: DetectorRow[] = [
      { key: 'group:living_room', kind: 'group', group: makeGroup() },
      { key: 'sensor:sensor.living_room_temp', kind: 'sensor', entry: makeSensor() },
    ];
    const { container } = render(<DetectorList rows={rows} />);
    expect(container.querySelectorAll('.argus-list-row').length).toBe(2);
  });

  it('renders the custom empty-state branch for zero rows', () => {
    const { container } = render(<DetectorList rows={[]} />);
    expect(container.querySelector('.argus-empty')).not.toBeNull();
    expect(container.querySelector('.argus-card')).toBeNull();
    expect(container.textContent).toMatch(/No detectors configured/);
  });
});
