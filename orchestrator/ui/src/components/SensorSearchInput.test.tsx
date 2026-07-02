import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '@testing-library/preact';
import { fireEvent } from '@testing-library/preact';
import { SensorSearchInput } from './SensorSearchInput';

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
