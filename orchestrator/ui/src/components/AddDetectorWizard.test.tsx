import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/preact';
import { AddDetectorWizard } from './AddDetectorWizard';
import * as client from '../api/client';
import { sensors, entityEdits, save } from '../state/sensors';
import { pendingPrefillMembers } from '../state/groups';
import type { SensorEntry } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.living_room_temp',
    friendlyName: 'Living Room Temp',
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: false,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('AddDetectorWizard', () => {
  beforeEach(() => {
    sensors.value = [];
    entityEdits.value = {};
    pendingPrefillMembers.value = null;
    location.hash = '';
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('loads the full sensor set on mount (D-07) — api/sensors?q= with an empty term', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ entries: [] });

    render(<AddDetectorWizard />);

    await waitFor(() => expect(client.apiGet).toHaveBeenCalledWith('api/sensors?q='));
  });

  it('WIZ-02: selecting exactly 1 sensor and continuing tracks it and navigates to the sensor route', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [makeSensor({ entityId: 'sensor.living_room_temp' })],
    });

    const { container } = render(<AddDetectorWizard />);
    await waitFor(() => expect(sensors.value).toHaveLength(1));

    const input = container.querySelector('input.argus-search__input') as HTMLInputElement;
    fireEvent.input(input, { target: { value: 'living' } });

    const checkbox = await screen.findByLabelText('sensor.living_room_temp');
    fireEvent.click(checkbox);

    const continueBtn = screen.getByText('Configure detector');
    fireEvent.click(continueBtn);

    expect(location.hash).toBe('#/detectors/sensor/sensor.living_room_temp');
    expect(entityEdits.value['sensor.living_room_temp'].isTracked).toBe(true);
  });

  it('WIZ-03: selecting >=2 sensors and continuing pre-fills the group draft and navigates to /groups/new', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [
        makeSensor({ entityId: 'sensor.living_room_temp' }),
        makeSensor({ entityId: 'sensor.living_room_humidity', friendlyName: 'Living Room Humidity' }),
      ],
    });

    const { container } = render(<AddDetectorWizard />);
    await waitFor(() => expect(sensors.value).toHaveLength(2));

    const input = container.querySelector('input.argus-search__input') as HTMLInputElement;
    fireEvent.input(input, { target: { value: 'living' } });

    fireEvent.click(await screen.findByLabelText('sensor.living_room_temp'));
    fireEvent.click(await screen.findByLabelText('sensor.living_room_humidity'));

    const continueBtn = screen.getByText('Create group');
    fireEvent.click(continueBtn);

    expect(pendingPrefillMembers.value).toEqual([
      'sensor.living_room_temp',
      'sensor.living_room_humidity',
    ]);
    expect(location.hash).toBe('#/groups/new');
  });

  it('WIZ-04 (CRITICAL, D-07): tracking one new sensor after the full set is hydrated preserves every previously-tracked sensor in the save POST body', async () => {
    // Seed the full tracked set exactly as the wizard's own mount-time loadSensors('')
    // would — three sensors already tracked.
    vi.spyOn(client, 'apiGet').mockResolvedValue({
      entries: [
        makeSensor({ entityId: 'sensor.a', isTracked: true }),
        makeSensor({ entityId: 'sensor.b', isTracked: true }),
        makeSensor({ entityId: 'sensor.c', isTracked: true }),
        makeSensor({ entityId: 'sensor.new', isTracked: false }),
      ],
    });

    render(<AddDetectorWizard />);
    await waitFor(() => expect(sensors.value).toHaveLength(4));

    // Simulate the WIZ-02 1-sensor exit: track a fourth sensor on top of the hydrated set.
    entityEdits.value = {
      ...entityEdits.value,
      'sensor.new': { isTracked: true, detectors: entityEdits.value['sensor.new']?.detectors ?? [] },
    };

    let capturedBody: unknown = null;
    vi.spyOn(client, 'apiPost').mockImplementation(async (_url: string, body: unknown) => {
      capturedBody = body;
      return { ok: true, count: 4, hasHst: false };
    });

    await save();

    const entityIds = (capturedBody as { entities: { entityId: string }[] }).entities.map(
      (e) => e.entityId
    );
    expect(entityIds.sort()).toEqual(['sensor.a', 'sensor.b', 'sensor.c', 'sensor.new'].sort());
  });
});
