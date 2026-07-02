import type { DetectorCatalogEntry } from '../api/types';
import { draftParams } from '../state/groups';

interface AdvancedParamsDisclosureProps {
  entry: DetectorCatalogEntry;
}

// Native <details>/<summary> "Advanced — view/override parameters" (ALGO-02) — reuses the
// exact .argus-param-grid/.argus-param-field/.argus-param-field__input classes from Phase 7's
// DetectorParamGrid (same disclosure pattern as DetectorDisclosure, just a param grid for
// group-detector params instead of per-entity ones). Fields are pre-filled with the current
// expanded preset values; editing a field overrides only that key in the draft — the preset
// radio selection itself is not cleared (SensitivityPresetPicker's "customized" indicator
// communicates the divergence).
export function AdvancedParamsDisclosure({ entry }: AdvancedParamsDisclosureProps) {
  function updateParam(key: string, value: string) {
    draftParams.value = { ...draftParams.value, [key]: value };
  }

  return (
    <details class="argus-detectors-details">
      <summary class="argus-disclosure-toggle">Advanced — view/override parameters</summary>
      <div class="argus-param-grid">
        {entry.paramSchema.map((field) => {
          const inputId = `group-param-${entry.name}-${field.key}`;
          return (
            <div key={field.key} class="argus-param-field">
              <label class="argus-param-field__label" for={inputId}>
                {field.key}
              </label>
              <input
                class="argus-param-field__input"
                type={field.type === 'number' ? 'number' : 'text'}
                id={inputId}
                min={field.min ?? undefined}
                max={field.max ?? undefined}
                step={field.step ?? undefined}
                value={draftParams.value[field.key] ?? ''}
                onInput={(e) => updateParam(field.key, (e.target as HTMLInputElement).value)}
              />
            </div>
          );
        })}
      </div>
    </details>
  );
}
