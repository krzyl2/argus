import type { DetectorCatalogEntry } from '../api/types';
import { draftParams } from '../state/groups';
import { Input } from './Input';

interface AdvancedParamsDisclosureProps {
  entry: DetectorCatalogEntry;
}

// Native <details>/<summary> "Advanced — view/override parameters" (ALGO-02) — field
// rows follow the same external-label + shared-Input convention as Phase 12's
// DetectorParamGrid (.argus-param-grid/.argus-param-field, Input owns .argus-param-field__input).
// Fields are pre-filled with the current expanded preset values; editing a field overrides
// only that key in the draft — the preset radio selection itself is not cleared
// (SensitivityPresetPicker's "customized" indicator communicates the divergence).
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
              <Input
                id={inputId}
                type={field.type === 'number' ? 'number' : 'text'}
                step={field.step ?? undefined}
                value={draftParams.value[field.key] ?? ''}
                onChange={(v) => updateParam(field.key, v)}
              />
            </div>
          );
        })}
      </div>
    </details>
  );
}
