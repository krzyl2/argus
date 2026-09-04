import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, fireEvent } from '@testing-library/preact';
import { DetectorParamGrid } from './DetectorParamGrid';
import { detectorDefaults, resetDetectorDefaults } from '../state/detectorDefaults';
import type { DetectorEntry } from '../api/types';

function makeDetector(overrides: Partial<DetectorEntry> = {}): DetectorEntry {
  return {
    name: 'mad',
    params: { threshold: '3.5', window: '10' },
    ...overrides,
  };
}

const noop = () => {};

const RMAD_DEFAULTS: Record<string, string> = {
  window: '720',
  min_samples: '60',
  z_scale: '5.0',
  scale_floor: '0.0',
  high_threshold: '0.5',
  low_threshold: '0.375',
  min_consecutive: '3',
  frozen_window: '10',
  frozen_variance_threshold: '0.0',
};

function makeRmad(overrides: Record<string, string> = {}): DetectorEntry {
  return { name: 'rmad', params: { ...RMAD_DEFAULTS, ...overrides } };
}

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

  // The rmad thresholds are DIMENSIONLESS on purpose (D-B) — that is what makes one default
  // table correct on every sensor, and it is also what makes a bare "0.5" unreadable. The help
  // line converts it back: z = z_scale * t / (1 - t), so 0.5 -> 5 and 0.615 -> 8. Without this
  // the operator has no way to reason about whether the threshold suits their sensor.
  it('RmadHighThreshold_ShowsTheRobustZItMeans', () => {
    const { container: c1 } = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeRmad({ high_threshold: '0.5' })}
        onParamChange={noop}
      />
    );
    expect(c1.textContent).toMatch(/5,0σ/);

    const { container: c2 } = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeRmad({ high_threshold: '0.615' })}
        onParamChange={noop}
      />
    );
    expect(c2.textContent).toMatch(/8,0σ/);
  });

  // §7 #14, unresolved by design: the window is measured in SAMPLES, so two sensors on
  // identical params have completely different clock memory. 720 samples is ~3 h on
  // memory_use_percent (15,3 s/reading) and ~78 h on lodowkababcia_power (391 s/reading). The
  // wall-clock readout is the only mitigation shipped for that, so the warning must fire on the
  // slow sensor and stay silent on the fast one — a warning on both would be ignored on both.
  it('WindowField_ShowsWallClockSpan_AndWarnsBeyond48h', () => {
    const slow = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeRmad({ window: '720' })}
        onParamChange={noop}
        ctx={{ medianIntervalSec: 391, zScale: 5, unitOfMeasurement: 'W' }}
      />
    );
    expect(slow.container.textContent).toMatch(/78,2 h/);
    expect(slow.container.querySelector('.argus-param-field__warn')).not.toBeNull();
    expect(slow.container.textContent).toMatch(/240/);

    const fast = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeRmad({ window: '720' })}
        onParamChange={noop}
        ctx={{ medianIntervalSec: 15.3, zScale: 5, unitOfMeasurement: '%' }}
      />
    );
    expect(fast.container.textContent).toMatch(/3,1 h/);
    expect(fast.container.querySelector('.argus-param-field__warn')).toBeNull();
  });

  // A stored block that omits a key (`params: {}` is the fresh-install shape) must not read as
  // an empty form. The key is not written back -- the input stays empty, so nothing is
  // materialized onto disk on the next Save -- but the operator has to be able to SEE the value
  // that is actually in force, and the help line has to describe that value and not a blank.
  describe('omitted keys render as the server default, without adopting it', () => {
    afterEach(() => {
      resetDetectorDefaults();
    });

    it('shows the default as a placeholder and leaves the input empty', () => {
      detectorDefaults.value = { rmad: RMAD_DEFAULTS };

      const { container } = render(
        <DetectorParamGrid
          entityIdx={0}
          detIdx={0}
          detector={{ name: 'rmad', params: {} }}
          onParamChange={noop}
          ctx={{ medianIntervalSec: 15.3, zScale: 5, unitOfMeasurement: '%' }}
        />
      );

      const windowInput = container.querySelector('#param-0-0-window') as HTMLInputElement;
      expect(windowInput.value).toBe('');
      expect(windowInput.getAttribute('placeholder')).toBe('720');

      // The help line describes the effective window (720 samples at 15.3 s), not a blank.
      const windowField = windowInput.closest('.argus-param-field')!;
      expect(windowField.querySelector('.argus-param-field__help')!.textContent).toMatch(/3,1 h/);

      // And an omitted key is not an error: nothing here may be marked invalid.
      expect(container.querySelectorAll('.argus-param-field--error')).toHaveLength(0);
    });

    // The mirror image of the two tests above, and the reason the merge cannot be dropped: a
    // partial block has to be checked AGAINST the defaults that fill its gaps, not on its own.
    // min_samples and window are compared to each other, so a block that stores only one of
    // them is validated against a value that lives in the default table -- validate the raw
    // stored map instead and the grid marks nothing, showing a green form for a configuration
    // the server rejects.
    it('marks a stored key that is illegal against the default it is compared to', () => {
      detectorDefaults.value = { rmad: RMAD_DEFAULTS }; // window 720

      const { container, getByText } = render(
        <DetectorParamGrid
          entityIdx={0}
          detIdx={0}
          detector={{ name: 'rmad', params: { min_samples: '900' } }}
          onParamChange={noop}
        />
      );

      const minSamples = container.querySelector('#param-0-0-min_samples') as HTMLInputElement;
      expect(minSamples.getAttribute('aria-invalid')).toBe('true');
      expect(getByText('Must not be greater than window.')).toBeTruthy();
      expect(container.querySelectorAll('.argus-param-field--error')).toHaveLength(1);
    });

    it('marks a stored threshold that crosses the default on the other side of the pair', () => {
      detectorDefaults.value = { rmad: RMAD_DEFAULTS }; // high_threshold 0.5

      const { container } = render(
        <DetectorParamGrid
          entityIdx={0}
          detIdx={0}
          detector={{ name: 'rmad', params: { low_threshold: '0.9' } }}
          onParamChange={noop}
        />
      );

      const low = container.querySelector('#param-0-0-low_threshold') as HTMLInputElement;
      const high = container.querySelector('#param-0-0-high_threshold') as HTMLInputElement;
      expect(low.getAttribute('aria-invalid')).toBe('true');
      expect(high.getAttribute('aria-invalid')).toBe('true');
      expect(container.querySelectorAll('.argus-param-field--error')).toHaveLength(2);
    });

    it('degrades to an empty field, never to an error, when the defaults table is missing', () => {
      const { container } = render(
        <DetectorParamGrid
          entityIdx={0}
          detIdx={0}
          detector={{ name: 'rmad', params: {} }}
          onParamChange={noop}
        />
      );

      expect(container.querySelectorAll('.argus-param-field--error')).toHaveLength(0);
      const windowInput = container.querySelector('#param-0-0-window') as HTMLInputElement;
      expect(windowInput.value).toBe('');
    });
  });

  // Without a measured cadence the UI must say samples and nothing more. Inventing a span from
  // an assumed reading rate is exactly the "~4 minutes" mistake the save banner used to make.
  it('WindowField_WithoutMeasuredCadence_SaysNothingAboutTime', () => {
    const { container } = render(
      <DetectorParamGrid
        entityIdx={0}
        detIdx={0}
        detector={makeRmad({ window: '720' })}
        onParamChange={noop}
      />
    );
    const windowField = container.querySelector('#param-0-0-window')!.closest('.argus-param-field')!;
    expect(windowField.querySelector('.argus-param-field__help')).toBeNull();
  });
});
