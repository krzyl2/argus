export interface InputProps {
  value: string;
  onChange: (value: string) => void;
  type?: string;
  placeholder?: string;
  ariaLabel?: string;
  disabled?: boolean;
  invalid?: boolean;
}

// Thin wrapper over .argus-param-field__input. Focus handling (including
// keyboard focus-visible) is owned entirely by the global stylesheet.
export function Input({
  value,
  onChange,
  type = 'text',
  placeholder,
  ariaLabel,
  disabled,
  invalid,
}: InputProps) {
  return (
    <input
      class="argus-param-field__input"
      type={type}
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      aria-invalid={invalid ? 'true' : 'false'}
      disabled={disabled}
      onInput={(e) => onChange((e.target as HTMLInputElement).value)}
    />
  );
}
