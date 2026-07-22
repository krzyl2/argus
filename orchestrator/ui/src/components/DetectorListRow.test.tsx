import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/preact';
import { DetectorListRow } from './DetectorListRow';
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

describe('DetectorListRow', () => {
  it('group variant: Edit link points to #/groups/<encoded groupId>, no delete/untrack control (D-04/D-08a)', () => {
    const row: DetectorRow = { key: 'group:living_room', kind: 'group', group: makeGroup() };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    const link = screen.getByText('Edit') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('#/groups/living_room');
    expect(screen.queryByText(/delete/i)).toBeNull();
    expect(screen.queryByText(/untrack/i)).toBeNull();
  });

  it('group variant: encodes a groupId with characters that need escaping', () => {
    const row: DetectorRow = {
      key: 'group:living room',
      kind: 'group',
      group: makeGroup({ groupId: 'living room' }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    const link = screen.getByText('Edit') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe(`#/groups/${encodeURIComponent('living room')}`);
  });

  it('sensor variant: Edit link points to #/detectors/sensor/<encoded entityId>, no checkbox, no untrack/delete control (D-03/D-08a)', () => {
    const row: DetectorRow = {
      key: 'sensor:sensor.living_room_temp',
      kind: 'sensor',
      entry: makeSensor(),
    };
    const { container } = render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    const link = screen.getByText('Edit') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('#/detectors/sensor/sensor.living_room_temp');
    expect(container.querySelector('input.argus-checkbox')).toBeNull();
    expect(screen.queryByText(/untrack/i)).toBeNull();
    expect(screen.queryByText(/delete/i)).toBeNull();
  });

  it('sensor variant: warming shows "Rozgrzewka N/window" and not Działa (QUICK-warmup-status)', () => {
    const row: DetectorRow = {
      key: 'sensor:sensor.living_room_temp',
      kind: 'sensor',
      entry: makeSensor({ warmedUp: false, readingCount: 100, warmUpWindow: 250 }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText(/Rozgrzewka\s*100\/250/)).not.toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });

  it('sensor variant: warmed up shows "Działa" and not Rozgrzewka (QUICK-warmup-status)', () => {
    const row: DetectorRow = {
      key: 'sensor:sensor.living_room_temp',
      kind: 'sensor',
      entry: makeSensor({ warmedUp: true, readingCount: 250, warmUpWindow: 250 }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText('Działa')).not.toBeNull();
    expect(screen.queryByText(/Rozgrzewka/)).toBeNull();
  });

  it('sensor variant: no status data renders neither chip (QUICK-warmup-status)', () => {
    const row: DetectorRow = {
      key: 'sensor:sensor.living_room_temp',
      kind: 'sensor',
      entry: makeSensor(),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.queryByText(/Rozgrzewka/)).toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });

  it('group variant: never renders a warm-up chip (QUICK-warmup-status)', () => {
    const row: DetectorRow = { key: 'group:living_room', kind: 'group', group: makeGroup() };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.queryByText(/Rozgrzewka/)).toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });
});
