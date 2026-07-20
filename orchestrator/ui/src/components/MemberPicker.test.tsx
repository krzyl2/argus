import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { MemberPicker } from './MemberPicker';
import type { SensorEntry } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.living_room_temp',
    friendlyName: 'Living Room Temp',
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

const SENSORS: SensorEntry[] = [
  makeSensor({ entityId: 'sensor.living_room_temp', friendlyName: 'Living Room Temp' }),
  makeSensor({ entityId: 'sensor.living_room_humidity', friendlyName: 'Living Room Humidity' }),
];

describe('MemberPicker', () => {
  it('gates rows behind MIN_QUERY_LENGTH — renders no rows and the "type at least" message for a short query', () => {
    const { container } = render(
      <MemberPicker
        sensors={SENSORS}
        selectedIds={[]}
        mode="peer_divergence"
        query="l"
        onQueryChange={() => {}}
        onToggleMember={() => {}}
      />
    );
    expect(container.querySelectorAll('.argus-list-row').length).toBe(0);
    expect(screen.getByText(/Type at least 2 characters/)).toBeTruthy();
  });

  it('renders one row per match via the shared Checkbox once the query is long enough', () => {
    const { container } = render(
      <MemberPicker
        sensors={SENSORS}
        selectedIds={[]}
        mode="peer_divergence"
        query="living"
        onQueryChange={() => {}}
        onToggleMember={() => {}}
      />
    );
    const rows = container.querySelectorAll('.argus-list-row');
    expect(rows.length).toBe(2);
    expect(container.querySelectorAll('input.argus-checkbox').length).toBe(2);
    expect(container.querySelector('.argus-card ul.argus-list')).not.toBeNull();
  });

  it('renders a member Badge for selected entries', () => {
    const { container } = render(
      <MemberPicker
        sensors={SENSORS}
        selectedIds={['sensor.living_room_temp']}
        mode="peer_divergence"
        query="living"
        onQueryChange={() => {}}
        onToggleMember={() => {}}
      />
    );
    expect(screen.getByText('member')).toBeTruthy();
    expect(container.querySelectorAll('.argus-pill--member').length).toBe(1);
  });

  it('calls onToggleMember with the entityId and new checked state when a checkbox is toggled', () => {
    const onToggleMember = vi.fn();
    render(
      <MemberPicker
        sensors={SENSORS}
        selectedIds={[]}
        mode="peer_divergence"
        query="living"
        onQueryChange={() => {}}
        onToggleMember={onToggleMember}
      />
    );
    const checkbox = screen.getByLabelText('sensor.living_room_temp') as HTMLInputElement;
    fireEvent.click(checkbox);
    expect(onToggleMember).toHaveBeenCalledWith('sensor.living_room_temp', true);
  });
});
