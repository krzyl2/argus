import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '@testing-library/preact';
import { fireEvent } from '@testing-library/preact';
import { SensorSearchInput } from './SensorSearchInput';
import { matchesSensorQuery } from './sensorMatch';
import type { SensorEntry } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.abc123',
    friendlyName: null,
    currentValue: '1',
    unitOfMeasurement: null,
    isTracked: false,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('SensorSearchInput placeholder (SRCH-01)', () => {
  it('renders the updated "name or entity ID" placeholder', () => {
    const { container } = render(<SensorSearchInput value="" onChange={() => {}} />);
    const input = container.querySelector('input') as HTMLInputElement;
    expect(input.placeholder).toBe('Filter by name or entity ID…');
  });
});

describe('matchesSensorQuery (SRCH-01 predicate)', () => {
  it('matches on friendly_name when entity_id does not match', () => {
    const entry = makeSensor({ entityId: 'sensor.abc123', friendlyName: 'Living Room Temp' });
    expect(matchesSensorQuery(entry, 'Living Room')).toBe(true);
  });

  it('still matches on entity_id (zero regression on Phase 7 behavior)', () => {
    const entry = makeSensor({ entityId: 'sensor.outdoor_temp', friendlyName: null });
    expect(matchesSensorQuery(entry, 'outdoor')).toBe(true);
  });

  it('is case-insensitive for friendly_name', () => {
    const entry = makeSensor({ entityId: 'sensor.abc123', friendlyName: 'Bedroom Humidity' });
    expect(matchesSensorQuery(entry, 'bedroom')).toBe(true);
  });

  it('returns false when neither field matches', () => {
    const entry = makeSensor({ entityId: 'sensor.abc123', friendlyName: 'Bedroom Humidity' });
    expect(matchesSensorQuery(entry, 'kitchen')).toBe(false);
  });

  it('returns true for an empty query (no filter)', () => {
    const entry = makeSensor();
    expect(matchesSensorQuery(entry, '')).toBe(true);
  });
});

describe('SensorSearchInput debounce cleanup', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('does not call onChange after unmount once the debounce timer would have fired', () => {
    const onChange = vi.fn();
    const { unmount, container } = render(
      <SensorSearchInput value="" onChange={onChange} />
    );

    const input = container.querySelector('input') as HTMLInputElement;
    fireEvent.input(input, { target: { value: 'temp' } });

    // Unmount before the 200ms debounce fires.
    unmount();

    vi.advanceTimersByTime(500);

    expect(onChange).not.toHaveBeenCalled();
  });

  it('still calls onChange normally when not unmounted', () => {
    const onChange = vi.fn();
    const { container } = render(<SensorSearchInput value="" onChange={onChange} />);

    const input = container.querySelector('input') as HTMLInputElement;
    fireEvent.input(input, { target: { value: 'temp' } });

    vi.advanceTimersByTime(200);

    expect(onChange).toHaveBeenCalledWith('temp');
  });
});
