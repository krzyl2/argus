export interface CheckboxProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  ariaLabel?: string;
  disabled?: boolean;
}

// Thin wrapper over .argus-checkbox (mirrors SensorListRow's raw checkbox usage).
export function Checkbox({ checked, onChange, ariaLabel, disabled }: CheckboxProps) {
  return (
    <input
      class="argus-checkbox"
      type="checkbox"
      checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      onChange={(e) => onChange((e.target as HTMLInputElement).checked)}
    />
  );
}
