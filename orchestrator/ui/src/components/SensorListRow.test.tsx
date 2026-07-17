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
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

const noop = () => {};

interface RowOverrides {
  entry?: SensorEntry;
  isTracked?: boolean;
  isSelected?: boolean;
  onSelectRow?: () => void;
  onToggleTracked?: (checked: boolean) => void;
}

function renderRow(overrides: RowOverrides = {}) {
  return render(
    <ul>
      <SensorListRow
        entry={overrides.entry ?? makeEntry()}
        entityIdx={0}
        isTracked={overrides.isTracked ?? false}
        isSelected={overrides.isSelected ?? false}
        onSelectRow={overrides.onSelectRow ?? noop}
        detectors={[]}
        onToggleTracked={overrides.onToggleTracked ?? noop}
        onDetectorTypeChange={noop}
        onDetectorParamChange={noop}
        onDetectorRemove={noop}
        onDetectorAdd={noop}
      />
    </ul>
  );
}

describe('SensorListRow friendly name rule', () => {
  it('renders friendly name when present and different from entity_id', () => {
    const { container } = renderRow({
      entry: makeEntry({ friendlyName: 'Living Room Temp' }),
    });
    const el = container.querySelector('.argus-row-friendly-name');
    expect(el).not.toBeNull();
    expect(el?.textContent).toBe('Living Room Temp');
  });

  it('hides friendly name when equal to entity_id', () => {
    const { container } = renderRow({
      entry: makeEntry({ friendlyName: 'sensor.living_room_temp' }),
    });
    expect(container.querySelector('.argus-row-friendly-name')).toBeNull();
  });

  it('hides friendly name when null/empty', () => {
    const { container } = renderRow({ entry: makeEntry({ friendlyName: null }) });
    expect(container.querySelector('.argus-row-friendly-name')).toBeNull();
  });
});

describe('SensorListRow tracked state', () => {
  it('applies argus-list-row--tracked class when isTracked', () => {
    const { container } = renderRow({ isTracked: true });
    expect(container.querySelector('.argus-list-row--tracked')).not.toBeNull();
  });

  it('does not apply tracked class when untracked', () => {
    const { container } = renderRow({ isTracked: false });
    expect(container.querySelector('.argus-list-row--tracked')).toBeNull();
  });

  it('calls onToggleTracked when checkbox is clicked', () => {
    const onToggleTracked = vi.fn();
    const { container } = renderRow({ onToggleTracked });
    const checkbox = container.querySelector('.argus-checkbox') as HTMLInputElement;
    checkbox.click();
    expect(onToggleTracked).toHaveBeenCalledWith(true);
  });
});

describe('SensorListRow selection (D-04)', () => {
  it('applies argus-list-row--selected class when isSelected', () => {
    const { container } = renderRow({ isSelected: true });
    expect(container.querySelector('.argus-list-row--selected')).not.toBeNull();
  });

  it('does not apply argus-list-row--selected class when not selected', () => {
    const { container } = renderRow({ isSelected: false });
    expect(container.querySelector('.argus-list-row--selected')).toBeNull();
  });

  it('calls onSelectRow when the row content is clicked', () => {
    const onSelectRow = vi.fn();
    const { container } = renderRow({ onSelectRow });
    const row = container.querySelector('.argus-list-row') as HTMLElement;
    row.click();
    expect(onSelectRow).toHaveBeenCalledTimes(1);
  });

  it('renders the DetectorDisclosure editor only when selected AND tracked', () => {
    const { container: selectedTracked } = renderRow({ isSelected: true, isTracked: true });
    expect(selectedTracked.querySelector('.argus-detectors-details')).not.toBeNull();

    const { container: selectedUntracked } = renderRow({ isSelected: true, isTracked: false });
    expect(selectedUntracked.querySelector('.argus-detectors-details')).toBeNull();

    const { container: unselectedTracked } = renderRow({ isSelected: false, isTracked: true });
    expect(unselectedTracked.querySelector('.argus-detectors-details')).toBeNull();
  });

  it('clicking the tracked checkbox toggles tracked state without firing onSelectRow (stopPropagation)', () => {
    const onToggleTracked = vi.fn();
    const onSelectRow = vi.fn();
    const { container } = renderRow({ onToggleTracked, onSelectRow });
    const checkbox = container.querySelector('.argus-checkbox') as HTMLInputElement;
    checkbox.click();
    expect(onToggleTracked).toHaveBeenCalledWith(true);
    expect(onSelectRow).not.toHaveBeenCalled();
  });

  it('renders the shared Badge (not argus-pill--tracked directly) for a tracked row', () => {
    const { container } = renderRow({ isTracked: true });
    const badge = container.querySelector('.argus-pill.argus-pill--tracked');
    expect(badge).not.toBeNull();
    expect(badge?.textContent).toBe('tracked');
  });
});
