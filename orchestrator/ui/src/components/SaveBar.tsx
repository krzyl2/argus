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
      <span id="argus-spinner" aria-hidden="true" class={saving ? 'htmx-request' : ''} />
      <button
        type="button"
        class="argus-btn argus-btn--primary"
        disabled={disabled}
        onClick={onSave}
      >
        Save configuration
      </button>
    </div>
  );
}
