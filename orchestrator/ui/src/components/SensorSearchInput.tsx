import { useEffect, useRef } from 'preact/hooks';

interface SensorSearchInputProps {
  value: string;
  onChange: (value: string) => void;
}

const DEBOUNCE_MS = 200;

// Replaces <input class="argus-search__input"> (htmx keyup changed delay:200ms).
export function SensorSearchInput({ value, onChange }: SensorSearchInputProps) {
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Clear any pending debounce timer on unmount so onChange never fires for an
  // inactive/unmounted view (e.g. once a second route is added).
  useEffect(() => () => {
    if (timerRef.current) clearTimeout(timerRef.current);
  }, []);

  function handleInput(e: Event) {
    const next = (e.target as HTMLInputElement).value;
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => onChange(next), DEBOUNCE_MS);
  }

  return (
    <div class="argus-search">
      <input
        class="argus-search__input"
        type="search"
        defaultValue={value}
        placeholder="Filter by entity ID…"
        aria-label="Filter entities"
        onInput={handleInput}
      />
    </div>
  );
}
