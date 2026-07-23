import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/preact';
import { DetectorListRow } from './DetectorListRow';
import type { DetectorRow } from '../state/detectors';
import type { GroupConfig, GroupStatus, SensorEntry } from '../api/types';

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

function makeStatus(overrides: Partial<GroupStatus> = {}): GroupStatus {
  return {
    groupId: 'living_room',
    score: 0.5,
    isAnomaly: false,
    detector: 'peer_divergence',
    scoredAtUtc: '2026-07-23T00:00:00Z',
    contributions: [],
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

  // Intent: a group row surfaces the last verdict from GET api/groups/{id}/status so the
  // operator sees at a glance whether a group is waiting, healthy, or anomalous.
  it('group status: undefined renders no status badge (not yet fetched)', () => {
    const row: DetectorRow = { key: 'group:living_room', kind: 'group', group: makeGroup() };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.queryByText('Oczekuje')).toBeNull();
    expect(screen.queryByText('Anomalia')).toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });

  it('group status: null renders "Oczekuje" (fetched, never scored)', () => {
    const row: DetectorRow = {
      key: 'group:living_room',
      kind: 'group',
      group: makeGroup(),
      status: null,
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText('Oczekuje')).not.toBeNull();
    expect(screen.queryByText('Anomalia')).toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });

  it('group status: scored non-anomaly renders "Działa"', () => {
    const row: DetectorRow = {
      key: 'group:living_room',
      kind: 'group',
      group: makeGroup(),
      status: makeStatus({ isAnomaly: false }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText('Działa')).not.toBeNull();
    expect(screen.queryByText('Oczekuje')).toBeNull();
    expect(screen.queryByText('Anomalia')).toBeNull();
  });

  it('group status: isAnomaly true renders "Anomalia"', () => {
    const row: DetectorRow = {
      key: 'group:living_room',
      kind: 'group',
      group: makeGroup(),
      status: makeStatus({ isAnomaly: true }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText('Anomalia')).not.toBeNull();
    expect(screen.queryByText('Oczekuje')).toBeNull();
    expect(screen.queryByText('Działa')).toBeNull();
  });

  it('group status: member count is still rendered alongside the status badge', () => {
    const row: DetectorRow = {
      key: 'group:living_room',
      kind: 'group',
      group: makeGroup(),
      status: makeStatus({ isAnomaly: true }),
    };
    render(
      <ul>
        <DetectorListRow row={row} />
      </ul>
    );
    expect(screen.getByText('2 members')).not.toBeNull();
  });
});
