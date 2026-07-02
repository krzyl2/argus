import { useEffect } from 'preact/hooks';
import type { DetectorCatalogEntry } from '../api/types';
import { draftParams, draftPresetLabel } from '../state/groups';

interface SensitivityPresetPickerProps {
  entry: DetectorCatalogEntry;
}

const DEFAULT_LABEL = 'Med';

/** True when every key in a preset's params matches the current draft params exactly. */
function matchesPreset(preset: Record<string, string>, current: Record<string, string>): boolean {
  return Object.entries(preset).every(([key, value]) => current[key] === value);
}

/**
 * Returns true if >=1 field in the draft diverges from the named preset's expansion —
 * drives the "Med, customized" inline indicator (ALGO-01/02, never hidden once Advanced
 * is closed).
 */
export function isCustomized(entry: DetectorCatalogEntry, label: string | null, current: Record<string, string>): boolean {
  if (!label) return false;
  const preset = entry.presets.find((p) => p.label === label);
  if (!preset) return false;
  return !matchesPreset(preset.params, current);
}

// Native radio group Low/Med/High (ALGO-01) — accent-color var, Med default. Selecting
// immediately (client-side, no round-trip) expands that preset's catalog params into the
// draft params (08-UI-SPEC.md Preset + Advanced Interaction Contract #1-2). Raw values are
// not shown here — AdvancedParamsDisclosure is the only place params are visible/editable.
export function SensitivityPresetPicker({ entry }: SensitivityPresetPickerProps) {
  // On mount for a detector with no preset label yet (fresh selection or an existing
  // group's saved params), default to Med — expand it unless the current params already
  // exactly match some other preset (then adopt that preset's label instead of clobbering
  // an existing group's saved values silently).
  useEffect(() => {
    if (draftPresetLabel.value) return;
    const matched = entry.presets.find((p) => matchesPreset(p.params, draftParams.value));
    if (matched) {
      draftPresetLabel.value = matched.label;
      return;
    }
    const med = entry.presets.find((p) => p.label === DEFAULT_LABEL) ?? entry.presets[0];
    if (med) {
      draftPresetLabel.value = med.label;
      draftParams.value = { ...med.params };
    }
    // Only re-run when the selected detector's catalog entry identity changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entry.name]);

  const activeLabel = draftPresetLabel.value ?? DEFAULT_LABEL;
  const customized = isCustomized(entry, draftPresetLabel.value, draftParams.value);

  function selectPreset(label: string) {
    const preset = entry.presets.find((p) => p.label === label);
    if (!preset) return;
    draftPresetLabel.value = label;
    draftParams.value = { ...preset.params };
  }

  return (
    <div class="argus-sensitivity-preset-picker">
      <div class="argus-sensitivity-preset-picker__options" role="radiogroup" aria-label="Sensitivity preset">
        {entry.presets.map((preset) => (
          <label key={preset.label} class="argus-sensitivity-preset-picker__option">
            <input
              type="radio"
              name="sensitivity-preset"
              value={preset.label}
              checked={activeLabel === preset.label}
              onChange={() => selectPreset(preset.label)}
            />
            <span class="argus-label">{preset.label}</span>
          </label>
        ))}
      </div>
      {customized && (
        <span class="argus-label argus-sensitivity-preset-picker__customized">
          {activeLabel}, customized
        </span>
      )}
    </div>
  );
}
