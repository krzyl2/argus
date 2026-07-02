import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/preact';
import { SensorListRow } from './SensorListRow';
import type { SensorEntry } from '../api/types';

function makeEntry(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.living_room_temp',
    friendlyName: null,
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: false,
    ...overrides,
  };
}

const noop = () => {};

describe('SensorListRow friendly name rule', () => {
  it('renders friendly name when present and different from entity_id', () => {
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry({ friendlyName: 'Living Room Temp' })}
          entityIdx={-1}
          isTracked={false}
          detectors={[]}
          onToggleTracked={noop}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    const el = container.querySelector('.argus-row-friendly-name');
    expect(el).not.toBeNull();
    expect(el?.textContent).toBe('Living Room Temp');
  });

  it('hides friendly name when equal to entity_id', () => {
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry({ friendlyName: 'sensor.living_room_temp' })}
          entityIdx={-1}
          isTracked={false}
          detectors={[]}
          onToggleTracked={noop}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    expect(container.querySelector('.argus-row-friendly-name')).toBeNull();
  });

  it('hides friendly name when null/empty', () => {
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry({ friendlyName: null })}
          entityIdx={-1}
          isTracked={false}
          detectors={[]}
          onToggleTracked={noop}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    expect(container.querySelector('.argus-row-friendly-name')).toBeNull();
  });
});

describe('SensorListRow tracked state', () => {
  it('applies argus-list-row--tracked class when isTracked', () => {
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry()}
          entityIdx={0}
          isTracked={true}
          detectors={[]}
          onToggleTracked={noop}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    expect(container.querySelector('.argus-list-row--tracked')).not.toBeNull();
  });

  it('does not apply tracked class when untracked', () => {
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry()}
          entityIdx={-1}
          isTracked={false}
          detectors={[]}
          onToggleTracked={noop}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    expect(container.querySelector('.argus-list-row--tracked')).toBeNull();
  });

  it('calls onToggleTracked when checkbox is clicked', () => {
    const onToggleTracked = vi.fn();
    const { container } = render(
      <ul>
        <SensorListRow
          entry={makeEntry()}
          entityIdx={-1}
          isTracked={false}
          detectors={[]}
          onToggleTracked={onToggleTracked}
          onDetectorTypeChange={noop}
          onDetectorParamChange={noop}
          onDetectorRemove={noop}
          onDetectorAdd={noop}
        />
      </ul>
    );
    const checkbox = container.querySelector('.argus-checkbox') as HTMLInputElement;
    checkbox.click();
    expect(onToggleTracked).toHaveBeenCalledWith(true);
  });
});
