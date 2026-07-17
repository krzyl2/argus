import type { DetectorEntry } from '../api/types';
import { validateDetectorParams } from '../validation/detectorParams';
import { FieldValidationError } from './FieldValidationError';
import { Input } from './Input';

interface DetectorParamGridProps {
  entityIdx: number;
  detIdx: number;
  detector: DetectorEntry;
  onParamChange: (key: string, value: string) => void;
}

interface FieldSpec {
  key: string;
  label: string;
  step?: string;
  span2?: boolean;
}

// Field set/order/defaults must match BuildHstParamGrid/BuildMadParamGrid/BuildStlParamGrid exactly.
const HST_FIELDS: FieldSpec[] = [
  { key: 'window', label: 'window' },
  { key: 'n_trees', label: 'n_trees' },
  { key: 'high_threshold', label: 'high_threshold', step: '0.01' },
  { key: 'low_threshold', label: 'low_threshold', step: '0.01' },
  { key: 'min_consecutive', label: 'min_consecutive' },
  { key: 'frozen_window', label: 'frozen_window' },
  { key: 'frozen_variance_threshold', label: 'frozen_variance', step: '0.0001', span2: true },
];

const MAD_FIELDS: FieldSpec[] = [
  { key: 'threshold', label: 'threshold', step: '0.1' },
  { key: 'window', label: 'window' },
];

const STL_FIELDS: FieldSpec[] = [
  { key: 'period', label: 'period' },
  { key: 'seasonal', label: 'seasonal' },
  { key: 'threshold', label: 'threshold', step: '0.1', span2: true },
];

function fieldsFor(name: 'hst' | 'mad' | 'stl'): FieldSpec[] {
  switch (name) {
    case 'mad':
      return MAD_FIELDS;
    case 'stl':
      return STL_FIELDS;
    default:
      return HST_FIELDS;
  }
}

export function DetectorParamGrid({ entityIdx, detIdx, detector, onParamChange }: DetectorParamGridProps) {
  const fields = fieldsFor(detector.name);
  const errors = validateDetectorParams(detector.name, detector.params);

  return (
    <div class="argus-param-grid">
      {fields.map((field) => {
        const inputId = `param-${entityIdx}-${detIdx}-${field.key}`;
        const error = errors[field.key];
        return (
          <div
            key={field.key}
            class={`argus-param-field${field.span2 ? ' argus-param-grid--span2' : ''}${
              error ? ' argus-param-field--error' : ''
            }`}
          >
            <label class="argus-param-field__label" for={inputId}>
              {field.label}
            </label>
            <Input
              id={inputId}
              value={detector.params[field.key] ?? ''}
              onChange={(v) => onParamChange(field.key, v)}
              type="number"
              step={field.step}
              invalid={!!error}
              ariaDescribedby={`${inputId}-err`}
              ariaLabel={field.label}
            />
            <FieldValidationError message={error} />
          </div>
        );
      })}
    </div>
  );
}
