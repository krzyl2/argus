import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/preact';
import { SingleDetectorEditorForm } from './SingleDetectorEditorForm';
import * as client from '../api/client';
import { sensors, entityEdits, saveState } from '../state/sensors';
import { draftDetector } from '../state/groups';
import type { SensorEntry } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.foo',
    friendlyName: 'Foo Sensor',
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('SingleDetectorEditorForm', () => {
  beforeEach(() => {
    sensors.value = [];
    entityEdits.value = {};
    saveState.value = 'idle';
    draftDetector.value = null;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('mounts loadSensors(\'\') (full set), renders the detector disclosure and a Save control', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ entries: [makeSensor()] });

    render(<SingleDetectorEditorForm entityId="sensor.foo" />);

    await waitFor(() => expect(sensors.value).toHaveLength(1));
    expect(client.apiGet).toHaveBeenCalledWith('api/sensors?q=');

    expect(screen.getByText('Foo Sensor')).toBeTruthy();
    expect(document.querySelector('.argus-detectors-details')).not.toBeNull();
    expect(document.querySelector('.argus-save-bar .argus-btn--primary')).not.toBeNull();
  });

  it('exposes an Untrack sensor control that flips entityEdits.isTracked to false', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ entries: [makeSensor()] });

    render(<SingleDetectorEditorForm entityId="sensor.foo" />);

    await waitFor(() => expect(sensors.value).toHaveLength(1));

    const untrackBtn = screen.getByText('Untrack sensor');
    expect(untrackBtn).toBeTruthy();
    fireEvent.click(untrackBtn);

    expect(entityEdits.value['sensor.foo'].isTracked).toBe(false);
  });

  it('never touches the group draft (Pitfall 6) — a pre-set draftDetector is left untouched after mount', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ entries: [makeSensor()] });
    draftDetector.value = 'ecod';

    render(<SingleDetectorEditorForm entityId="sensor.foo" />);

    await waitFor(() => expect(sensors.value).toHaveLength(1));

    expect(draftDetector.value).toBe('ecod');
  });
});
