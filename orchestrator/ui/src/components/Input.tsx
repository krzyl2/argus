export interface InputProps {
  value: string;
  onChange: (value: string) => void;
  type?: string;
  placeholder?: string;
  ariaLabel?: string;
  disabled?: boolean;
  invalid?: boolean;
  id?: string;
  step?: string;
  ariaDescribedby?: string;
}

// Thin wrapper over .argus-param-field__input. Focus handling (including
// keyboard focus-visible) is owned entirely by the global stylesheet.
//
// id/step/ariaDescribedby are additive optional passthroughs (12-RESEARCH.md Pattern 4,
// Pitfalls 2/4): id + ariaDescribedby preserve the screen-reader linkage to an external
// FieldValidationError message; step preserves numeric-spinner increments for threshold
// fields. All existing callers (SettingsPage.tsx) remain valid unchanged.
export function Input({
  value,
  onChange,
  type = 'text',
  placeholder,
  ariaLabel,
  disabled,
  invalid,
  id,
  step,
  ariaDescribedby,
}: InputProps) {
  return (
    <input
      id={id}
      class="argus-param-field__input"
      type={type}
      step={step}
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      aria-invalid={invalid ? 'true' : 'false'}
      aria-describedby={ariaDescribedby}
      disabled={disabled}
      onInput={(e) => onChange((e.target as HTMLInputElement).value)}
    />
  );
}
