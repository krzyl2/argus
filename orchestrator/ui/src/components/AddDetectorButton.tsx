interface AddDetectorButtonProps {
  entityId: string;
  onAdd: () => void;
}

// Replaces .argus-add-detector-row + "+ Add detector" button. Appends an HST-default
// detector client-side — no server round-trip (07-UI-SPEC note).
export function AddDetectorButton({ entityId, onAdd }: AddDetectorButtonProps) {
  return (
    <div class="argus-add-detector-row">
      <button
        type="button"
        class="argus-btn argus-btn--add-detector"
        aria-label={`Add detector to ${entityId}`}
        onClick={onAdd}
      >
        + Add detector
      </button>
    </div>
  );
}
