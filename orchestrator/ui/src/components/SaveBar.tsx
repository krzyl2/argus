import { Button } from './Button';

interface SaveBarProps {
  saving: boolean;
  disabled: boolean;
  onSave: () => void;
}

// Replaces #argus-save-bar. Spinner only during in-flight POST; button disabled while
// any field is in an error state (parity with inline JS usb() logic).
export function SaveBar({ saving, disabled, onSave }: SaveBarProps) {
  return (
    <div class="argus-save-bar">
      <Button variant="primary" loading={saving} disabled={disabled} onClick={onSave}>
        Save configuration
      </Button>
    </div>
  );
}
