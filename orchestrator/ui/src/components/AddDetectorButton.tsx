import { Button } from './Button';

interface AddDetectorButtonProps {
  entityId: string;
  onAdd: () => void;
}

// Replaces .argus-add-detector-row + "+ Add detector" button. Appends an HST-default
// detector client-side — no server round-trip (07-UI-SPEC note).
export function AddDetectorButton({ entityId, onAdd }: AddDetectorButtonProps) {
  return (
    <div class="argus-add-detector-row">
      <Button variant="secondary" onClick={onAdd} ariaLabel={`Add detector to ${entityId}`}>
        + Add detector
      </Button>
    </div>
  );
}
