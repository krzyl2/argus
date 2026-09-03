import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/preact';
import { SettingsPage } from './SettingsPage';
import * as client from '../api/client';
import { sensors, entityEdits, includePatterns, excludePatterns, saveState, save } from '../state/sensors';
import { settings, loadError } from '../state/settings';
import type { SensorEntry, SettingsResponse } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.a',
    friendlyName: null,
    currentValue: '1',
    unitOfMeasurement: null,
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

const SETTINGS_RESPONSE: SettingsResponse = {
  detectorEndpoint: null,
  influxUrl: null,
  influxBucket: null,
  batchIntervalMinutes: 60,
  nightlyFitHour: 3,
  logLevel: null,
};

// Routes the shared apiGet mock to the right fixture by URL, mirroring the
// real orchestrator: GET /api/settings (read-only sections) + GET /api/sensors
// (D-07 full-set mount guard for the relocated Pattern Filters section).
function mockApiGet(sensorEntries: SensorEntry[] = []) {
  return vi.spyOn(client, 'apiGet').mockImplementation(async (url: string) => {
    if (url === 'api/settings') return SETTINGS_RESPONSE as unknown;
    return { entries: sensorEntries } as unknown;
  });
}

describe('SettingsPage', () => {
  beforeEach(() => {
    settings.value = null;
    loadError.value = false;
    sensors.value = [];
    entityEdits.value = {};
    includePatterns.value = '';
    excludePatterns.value = '';
    saveState.value = 'idle';
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('D-08b: renders the relocated Pattern Filters section (include/exclude textareas)', async () => {
    mockApiGet();

    render(<SettingsPage />);

    const include = (await screen.findByLabelText('Include patterns')) as HTMLTextAreaElement;
    const exclude = screen.getByLabelText('Exclude patterns') as HTMLTextAreaElement;
    expect(include.id).toBe('include_patterns');
    expect(exclude.id).toBe('exclude_patterns');
  });

  it('D-07: mounts with a full-set sensors fetch (api/sensors?q=) before any save is possible', async () => {
    const apiGetSpy = mockApiGet();

    render(<SettingsPage />);

    await waitFor(() => expect(apiGetSpy).toHaveBeenCalledWith('api/sensors?q='));
  });

  it('WIZ-04 analog (CRITICAL, D-07): a pattern-filter-only save preserves the full previously-tracked set', async () => {
    // Seed the full tracked set exactly as SettingsPage's own mount-time
    // loadSensors('') would — three sensors already tracked, none touched here.
    mockApiGet([
      makeSensor({ entityId: 'sensor.a', isTracked: true }),
      makeSensor({ entityId: 'sensor.b', isTracked: true }),
      makeSensor({ entityId: 'sensor.c', isTracked: true }),
    ]);

    render(<SettingsPage />);
    await waitFor(() => expect(sensors.value).toHaveLength(3));

    // Edit only the pattern filters — no tracked-sensor toggling here.
    includePatterns.value = 'sensor.*temp*';

    let capturedBody: unknown = null;
    vi.spyOn(client, 'apiPost').mockImplementation(async (_url: string, body: unknown) => {
      capturedBody = body;
      return { ok: true, count: 3, hasStreaming: false };
    });

    await save();

    const entityIds = (capturedBody as { entities: { entityId: string }[] }).entities.map(
      (e) => e.entityId
    );
    expect(entityIds.sort()).toEqual(['sensor.a', 'sensor.b', 'sensor.c']);
    expect((capturedBody as { include: string }).include).toBe('sensor.*temp*');
  });
});
