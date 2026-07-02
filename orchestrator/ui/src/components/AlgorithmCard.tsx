import type { DetectorCatalogEntry, GroupDetectorName } from '../api/types';

interface AlgorithmCardProps {
  entry: DetectorCatalogEntry;
  selected: boolean;
  guidedRecommended: boolean;
  onSelect: (detector: GroupDetectorName) => void;
}

// One selectable algorithm — name + catalog "best for..." description (ALGO-03) + selected
// state. A guided-recommended card additionally shows the "Suggested based on your answer"
// label (ALGO-04) — never relies on color alone (accessibility/transparency requirement,
// 08-UI-SPEC.md Color contract). One click always calls onSelect, zero friction/no confirm
// (same class of action as Phase 7's "Remove detector").
export function AlgorithmCard({ entry, selected, guidedRecommended, onSelect }: AlgorithmCardProps) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      class={`argus-algorithm-card${selected ? ' argus-algorithm-card--selected' : ''}`}
      onClick={() => onSelect(entry.name)}
    >
      {guidedRecommended && (
        <span class="argus-label argus-algorithm-card__guided-label">
          Suggested based on your answer — you can pick a different algorithm below.
        </span>
      )}
      <span class="argus-body argus-algorithm-card__name">{entry.name}</span>
      <span class="argus-label argus-algorithm-card__best-for">{entry.bestFor}</span>
    </button>
  );
}
