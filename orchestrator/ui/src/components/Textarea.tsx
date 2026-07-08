export interface TextareaProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  ariaLabel?: string;
  rows?: number;
  mono?: boolean;
}

// Thin wrapper over .argus-filters__textarea (already monospace).
export function Textarea({ value, onChange, placeholder, ariaLabel, rows }: TextareaProps) {
  return (
    <textarea
      class="argus-filters__textarea"
      value={value}
      placeholder={placeholder}
      aria-label={ariaLabel}
      rows={rows}
      onInput={(e) => onChange((e.target as HTMLTextAreaElement).value)}
    />
  );
}
