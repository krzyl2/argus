import { SearchInput } from './SearchInput';

interface SensorSearchInputProps {
  value: string;
  onChange: (value: string) => void;
}

const DEBOUNCE_MS = 200;

// Thin instantiation of the shared SearchInput (Plan 10-02). Replaces
// <input class="argus-search__input"> (htmx keyup changed delay:200ms).
export function SensorSearchInput({ value, onChange }: SensorSearchInputProps) {
  return (
    <SearchInput
      value={value}
      onChange={onChange}
      placeholder="Filter by name or entity ID…"
      ariaLabel="Filter entities"
      debounceMs={DEBOUNCE_MS}
    />
  );
}
