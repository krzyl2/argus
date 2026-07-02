interface PatternFiltersPanelProps {
  include: string;
  exclude: string;
  onIncludeChange: (value: string) => void;
  onExcludeChange: (value: string) => void;
}

// Replaces .argus-filters two-column grid.
export function PatternFiltersPanel({
  include,
  exclude,
  onIncludeChange,
  onExcludeChange,
}: PatternFiltersPanelProps) {
  return (
    <>
      <div class="argus-filters">
        <div class="argus-filters__group">
          <label class="argus-filters__label argus-label" for="include_patterns">
            Include patterns
          </label>
          <textarea
            id="include_patterns"
            class="argus-filters__textarea"
            rows={4}
            placeholder="e.g. sensor.*temp*"
            value={include}
            onInput={(e) => onIncludeChange((e.target as HTMLTextAreaElement).value)}
          />
        </div>
        <div class="argus-filters__group">
          <label class="argus-filters__label argus-label" for="exclude_patterns">
            Exclude patterns
          </label>
          <textarea
            id="exclude_patterns"
            class="argus-filters__textarea"
            rows={4}
            placeholder="e.g. sensor.*test*"
            value={exclude}
            onInput={(e) => onExcludeChange((e.target as HTMLTextAreaElement).value)}
          />
        </div>
      </div>
      <p class="argus-label">One glob pattern per line. Manual selections override patterns.</p>
    </>
  );
}
