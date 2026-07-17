interface AlgorithmCardProps {
  name: string;
  bestFor: string;
  selected: boolean;
  recommended: boolean;
  onSelect: (name: string) => void;
}

// One selectable algorithm — name + "best for..." description (ALGO-03) + selected state.
// A recommended card additionally shows the "Suggested based on your answer" label (ALGO-04)
// — never relies on color alone (accessibility/transparency requirement, 08-UI-SPEC.md Color
// contract; SC3 for the single-sensor hst/mad/stl picker). One click always calls onSelect,
// zero friction/no confirm (same class of action as Phase 7's "Remove detector").
//
// Widened to plain-string props (SEN-02/D-02): callers narrow the string back to their own
// detector-name union at the call site (e.g. `onSelect={(name) => pick(name as GroupDetectorName)}`)
// — this component itself stays agnostic to any specific detector catalog.
export function AlgorithmCard({ name, bestFor, selected, recommended, onSelect }: AlgorithmCardProps) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      class={`argus-algorithm-card${selected ? ' argus-algorithm-card--selected' : ''}`}
      onClick={() => onSelect(name)}
    >
      {recommended && (
        <span class="argus-label argus-algorithm-card__guided-label">
          Suggested based on your answer — you can pick a different algorithm below.
        </span>
      )}
      <span class="argus-body argus-algorithm-card__name">{name}</span>
      <span class="argus-label argus-algorithm-card__best-for">{bestFor}</span>
    </button>
  );
}
