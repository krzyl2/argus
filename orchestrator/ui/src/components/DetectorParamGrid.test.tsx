import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/preact';
import { DetectorParamGrid } from './DetectorParamGrid';
import type { DetectorEntry } from '../api/types';

function makeDetector(overrides: Partial<DetectorEntry> = {}): DetectorEntry {
  return {
    name: 'mad',
    params: { threshold: '3.5', window: '10' },
    ...overrides,
  };
}

const noop = () => {};

describe('DetectorParamGrid (raw <input> -> shared Input, D-07)', () => {
  it('renders each field via the shared Input, which forwards field.label as aria-label (distinguishes the shared component from a bare hand-authored <input>)', () => {
    const { container } = render(
      <DetectorParamGrid entityIdx={0} detIdx={0} detector={makeDetector()} onParamChange={noop} />
    );

    const inputs = container.querySelectorAll('input.argus-param-field__input');
    expect(inputs).toHaveLength(2);
    const thresholdInput = container.querySelector('#param-0-0-threshold') as HTMLInputElement;
    expect(thresholdInput.getAttribute('aria-label')).toBe('threshold');
  });

  it('links aria-describedby to the FieldValidationError span id for each field', () => {
    const { container } = render(
      <DetectorParamGrid
        entityIdx={2}
        detIdx={1}
        detector={makeDetector({ params: { threshold: '', window: '10' } })}
        onParamChange={noop}
      />
    );

    const thresholdInput = container.querySelector('#param-2-1-threshold') as HTMLInputElement;
    expect(thresholdInput).toBeTruthy();
    expect(thresholdInput.getAttribute('aria-describedby')).toBe('param-2-1-threshold-err');
  });

  it('sets aria-invalid=true and renders the detectorParams.ts error message when a field is invalid', () => {
    const { container, getByText } = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeDetector({ params: { threshold: '', window: '10' } })}
        onParamChange={noop}
      />
    );

    const thresholdInput = container.querySelector('#param-0-0-threshold') as HTMLInputElement;
    expect(thresholdInput.getAttribute('aria-invalid')).toBe('true');
    expect(getByText('Must provide a value.')).toBeTruthy();
  });

  it('does not mark a valid field as invalid and renders no error message for it', () => {
    const { container } = render(
      <DetectorParamGrid entityIdx={0} detIdx={0} detector={makeDetector()} onParamChange={noop} />
    );

    const thresholdInput = container.querySelector('#param-0-0-threshold') as HTMLInputElement;
    expect(thresholdInput.getAttribute('aria-invalid')).toBe('false');
  });

  it('forwards the field step (threshold step=0.1) to the Input so the number spinner does not revert to step 1', () => {
    const { container } = render(
      <DetectorParamGrid entityIdx={0} detIdx={0} detector={makeDetector()} onParamChange={noop} />
    );

    const thresholdInput = container.querySelector('#param-0-0-threshold') as HTMLInputElement;
    expect(thresholdInput.getAttribute('step')).toBe('0.1');
  });

  it('calls onParamChange with the field key and new value on input', () => {
    const onParamChange = vi.fn();
    const { container } = render(
      <DetectorParamGrid entityIdx={0} detIdx={0} detector={makeDetector()} onParamChange={onParamChange} />
    );

    const windowInput = container.querySelector('#param-0-0-window') as HTMLInputElement;
    fireEvent.input(windowInput, { target: { value: '20' } });

    expect(onParamChange).toHaveBeenCalledWith('window', '20');
  });
});
