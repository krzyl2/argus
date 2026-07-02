interface EmptyStateProps {
  query: string;
}

// Replaces BuildEmptyState — copy verbatim from EntityPickerPage.cs.
export function EmptyState({ query }: EmptyStateProps) {
  if (query) {
    return (
      <div class="argus-empty">
        <p class="argus-body">No sensors match &quot;{query}&quot;.</p>
        <p class="argus-label">Try a different search term or clear the filter.</p>
      </div>
    );
  }

  return (
    <div class="argus-empty">
      <p class="argus-body">No sensors found.</p>
      <p class="argus-label">
        Argus has not yet received a sensor snapshot from Home Assistant. Check that the add-on
        can reach the Supervisor and that the detector is running.
      </p>
    </div>
  );
}
