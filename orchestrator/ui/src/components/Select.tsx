export interface SelectOption {
  value: string;
  label: string;
}

export interface SelectProps {
  value: string;
  onChange: (value: string) => void;
  options: SelectOption[];
  ariaLabel?: string;
  disabled?: boolean;
}

// Thin wrapper over .argus-detector-select.
export function Select({ value, onChange, options, ariaLabel, disabled }: SelectProps) {
  return (
    <select
      class="argus-detector-select"
      aria-label={ariaLabel}
      value={value}
      disabled={disabled}
      onChange={(e) => onChange((e.target as HTMLSelectElement).value)}
    >
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}
