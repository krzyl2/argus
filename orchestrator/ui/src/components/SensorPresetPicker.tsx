import type { DetectorPreset } from '../api/types';
import { detectorPresets } from '../state/detectorDefaults';

interface SensorPresetPickerProps {
  /** Current params of the detector being edited. */
  params: Record<string, string>;
  /** Writes a preset's two threshold keys into the entity's detector params. */
  onApply: (params: Record<string, string>) => void;
}

const DEFAULT_LABEL = 'Med';

/** True when every key in a preset matches the current params exactly. */
function matchesPreset(preset: Record<string, string>, current: Record<string, string>): boolean {
  return Object.entries(preset).every(([key, value]) => current[key] === value);
}

/**
 * Low/Med/High sensitivity picker for a SINGLE sensor's rmad detector.
 *
 * Structurally a copy of SensitivityPresetPicker rather than a reuse of it, and deliberately:
 * that component reads and writes `state/groups` module signals (draftParams /
 * draftPresetLabel), so mounting it on a per-entity screen would silently overwrite whatever
 * group draft the operator has open elsewhere (Pitfall 6). This one is a pure function of its
 * props and owns no module state.
 *
 * It also does NOT expand a preset on mount. SensitivityPresetPicker does, because a fresh
 * group has no params at all; a sensor here always has saved params, and writing over them on
 * mount would undo a hand-tuned threshold the moment the operator opened the screen to look.
 *
 * A preset moves exactly the two threshold keys — never window, min_samples or scale_floor,
 * which are measured in units this sensor owns (samples, sensor units).
 */
export function SensorPresetPicker({ params, onApply }: SensorPresetPickerProps) {
  const presets = detectorPresets.value;
  if (!presets || presets.length === 0) return null;

  const matched = presets.find((p: DetectorPreset) => matchesPreset(p.params, params));
  const activeLabel = matched?.label ?? null;

  return (
    <div class="argus-sensitivity-preset-picker">
      <div
        class="argus-sensitivity-preset-picker__options"
        role="radiogroup"
        aria-label="Czułość"
      >
        {presets.map((preset) => (
          <label key={preset.label} class="argus-sensitivity-preset-picker__option">
            <input
              type="radio"
              name="sensor-sensitivity-preset"
              value={preset.label}
              checked={activeLabel === preset.label}
              onChange={() => onApply({ ...preset.params })}
            />
            <span class="argus-label">{preset.label}</span>
          </label>
        ))}
      </div>
      {activeLabel === null && (
        // Never silently show Med as selected when the thresholds are something else — the
        // operator would then read the radio group as a statement about their config.
        <span class="argus-label argus-sensitivity-preset-picker__customized">
          {DEFAULT_LABEL}, customized
        </span>
      )}
    </div>
  );
}
