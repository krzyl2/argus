import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { SensorPresetPicker } from './SensorPresetPicker';
import { detectorPresets, resetDetectorDefaults } from '../state/detectorDefaults';
import { draftParams, draftPresetLabel } from '../state/groups';

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

describe('SensorPresetPicker', () => {
  beforeEach(() => {
    resetDetectorDefaults();
    detectorPresets.value = [
      { label: 'Low', params: { high_threshold: '0.615', low_threshold: '0.444' } },
      { label: 'Med', params: { high_threshold: '0.5', low_threshold: '0.375' } },
      { label: 'High', params: { high_threshold: '0.444', low_threshold: '0.286' } },
    ];
  });

  // A preset is a sensitivity control, not a reset button. window, min_samples and scale_floor
  // are measured in units this sensor owns (samples; sensor units) and their correct values
  // depend on a cadence that ranges from 15,3 s to 391 s per reading across real sensors — so
  // moving them from a radio button would retune the sensor memory, not its sensitivity.
  it('SelectingHigh_WritesOnlyThresholdKeys', () => {
    const onApply = vi.fn();
    render(<SensorPresetPicker params={{ ...RMAD_DEFAULTS }} onApply={onApply} />);

    fireEvent.click(screen.getByDisplayValue('High'));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply.mock.calls[0][0]).toEqual({
      high_threshold: '0.444',
      low_threshold: '0.286',
    });
  });

  // Pitfall 6: SensitivityPresetPicker reads and writes state/groups module signals, so
  // mounting it on a per-entity screen would clobber whatever group draft the operator has open
  // elsewhere. This component must be a pure function of its props.
  it('never touches the group draft signals', () => {
    draftParams.value = { threshold: '3.5' };
    draftPresetLabel.value = 'Med';

    const { unmount } = render(
      <SensorPresetPicker params={{ ...RMAD_DEFAULTS }} onApply={() => {}} />
    );
    fireEvent.click(screen.getByDisplayValue('Low'));
    unmount();

    expect(draftParams.value).toEqual({ threshold: '3.5' });
    expect(draftPresetLabel.value).toBe('Med');
  });

  // Unlike the group picker, this one must NOT expand a preset on mount: a sensor always has
  // saved params, so writing over them when the screen opens would undo a hand-tuned threshold
  // the moment the operator opened the editor to look at it.
  it('does not write anything on mount', () => {
    const onApply = vi.fn();
    render(<SensorPresetPicker params={{ ...RMAD_DEFAULTS }} onApply={onApply} />);

    expect(onApply).not.toHaveBeenCalled();
  });

  it('checks the preset whose thresholds the entity already carries', () => {
    render(<SensorPresetPicker params={{ ...RMAD_DEFAULTS }} onApply={() => {}} />);

    expect((screen.getByDisplayValue('Med') as HTMLInputElement).checked).toBe(true);
    expect((screen.getByDisplayValue('Low') as HTMLInputElement).checked).toBe(false);
  });

  // Showing Med as selected while the thresholds are something else would turn the radio group
  // into a false statement about the operator config.
  it('checks nothing and says customized for hand-tuned thresholds', () => {
    render(
      <SensorPresetPicker
        params={{ ...RMAD_DEFAULTS, high_threshold: '0.55', low_threshold: '0.4' }}
        onApply={() => {}}
      />
    );

    for (const label of ['Low', 'Med', 'High']) {
      expect((screen.getByDisplayValue(label) as HTMLInputElement).checked).toBe(false);
    }
    expect(screen.getByText(/customized/)).not.toBeNull();
  });

  it('renders nothing before the server presets arrive', () => {
    detectorPresets.value = null;
    const { container } = render(
      <SensorPresetPicker params={{ ...RMAD_DEFAULTS }} onApply={() => {}} />
    );

    expect(container.textContent).toBe('');
  });
});
