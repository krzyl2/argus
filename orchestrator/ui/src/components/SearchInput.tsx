import { useEffect, useRef } from 'preact/hooks';

export interface SearchInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  ariaLabel?: string;
  debounceMs?: number;
}

const DEFAULT_DEBOUNCE_MS = 200;

// Debounced search input with a leading ⌕ glyph, over .argus-search /
// .argus-search__input. Debounce/cleanup logic ported verbatim from
// SensorSearchInput so it can delegate to this component in Plan 10-06.
export function SearchInput({
  value,
  onChange,
  placeholder,
  ariaLabel,
  debounceMs = DEFAULT_DEBOUNCE_MS,
}: SearchInputProps) {
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Clear any pending debounce timer on unmount so onChange never fires for an
  // inactive/unmounted view.
  useEffect(() => () => {
    if (timerRef.current) clearTimeout(timerRef.current);
  }, []);

  function handleInput(e: Event) {
    const next = (e.target as HTMLInputElement).value;
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => onChange(next), debounceMs);
  }

  return (
    <div class="argus-search">
      <span aria-hidden="true">⌕</span>
      <input
        class="argus-search__input"
        type="search"
        defaultValue={value}
        placeholder={placeholder}
        aria-label={ariaLabel}
        onInput={handleInput}
      />
    </div>
  );
}
