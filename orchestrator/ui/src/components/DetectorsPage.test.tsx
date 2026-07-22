import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, waitFor } from '@testing-library/preact';
import { DetectorsPage } from './DetectorsPage';
import * as client from '../api/client';
import { groups } from '../state/groups';
import { sensors, entityEdits } from '../state/sensors';
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

// Routes the shared apiGet mock to the right fixture by URL, mirroring the real
// orchestrator: GET /api/groups + GET /api/sensors?q= (D-07 full-set mount guard).
function mockApiGet(groupList: GroupConfig[], sensorEntries: SensorEntry[]) {
  return vi.spyOn(client, 'apiGet').mockImplementation(async (url: string) => {
    if (url === 'api/groups') return { groups: groupList } as unknown;
    return { entries: sensorEntries } as unknown;
  });
}

describe('DetectorsPage', () => {
  beforeEach(() => {
    groups.value = [];
    sensors.value = [];
    entityEdits.value = {};
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('loads both api/groups and api/sensors (full set) on mount (DET-01/D-07)', async () => {
    const spy = mockApiGet([], []);

    render(<DetectorsPage />);

    await waitFor(() => expect(spy).toHaveBeenCalledWith('api/groups'));
    await waitFor(() => expect(spy).toHaveBeenCalledWith('api/sensors?q='));
  });

  it('renders one unified list containing both a group row and a tracked-sensor row (DET-01)', async () => {
    mockApiGet(
      [makeGroup()],
      [makeSensor({ isTracked: true })]
    );

    const { container } = render(<DetectorsPage />);

    await waitFor(() => expect(container.querySelectorAll('.argus-list-row').length).toBe(2));
  });

  it('renders the Add detector primary CTA to #/detectors/add', async () => {
    mockApiGet([], []);

    const { container } = render(<DetectorsPage />);

    await waitFor(() => {
      const cta = container.querySelector('a[href="#/detectors/add"]');
      expect(cta).not.toBeNull();
    });
  });

  // QUICK-warmup-status: verifies the polling wiring deterministically without fake timers
  // (vi.advanceTimersByTime desyncs from preact's microtask-scheduled rerender — 14-02
  // precedent). apiGet is already mocked above so no real fetch ever fires; this just
  // spies on the interval scheduling itself.
  describe('warm-up polling (QUICK-warmup-status)', () => {
    it('sets a 5s loadSensors("") interval on mount and clears it on unmount', async () => {
      const apiSpy = mockApiGet([], []);
      const setIntervalSpy = vi.spyOn(globalThis, 'setInterval');
      const clearIntervalSpy = vi.spyOn(globalThis, 'clearInterval');

      const { unmount } = render(<DetectorsPage />);

      await waitFor(() => expect(apiSpy).toHaveBeenCalledWith('api/sensors?q='));
      expect(setIntervalSpy).toHaveBeenCalledWith(expect.any(Function), 5000);
      const intervalId = setIntervalSpy.mock.results[0]?.value;

      unmount();

      expect(clearIntervalSpy).toHaveBeenCalledWith(intervalId);
    });
  });
});
