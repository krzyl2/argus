import type { SaveResponse } from '../api/types';

interface SaveResultBannerProps {
  result: SaveResponse;
}

// Replaces #argus-flash + Build*Banner methods. Branches on ok/kind discriminant,
// never string-sniffing. Copy verbatim from EntityPickerPage.cs.
export function SaveResultBanner({ result }: SaveResultBannerProps) {
  if (result.ok) {
    const entityWord = result.count === 1 ? 'entity' : 'entities';
    return (
      <div class="argus-banner argus-banner--success" role="status" aria-live="polite">
        Saved — pipeline active. {result.count} {entityWord} tracked.
        {result.hasHst && (
          <p class="argus-warmup-note">
            HST detectors need ~4 minutes of readings to warm up (window=250 at ~1 reading/s).
            Anomaly scores will be low until warm-up completes.
          </p>
        )}
      </div>
    );
  }

  if (result.kind === 'validation') {
    return (
      <div class="argus-banner argus-banner--validation" role="alert" aria-live="assertive">
        Save blocked: {result.errorCount} field(s) have invalid values. Correct the highlighted
        fields and try again.
      </div>
    );
  }

  return (
    <div class="argus-banner argus-banner--error" role="alert" aria-live="assertive">
      Save failed. {result.reason}. Check the add-on log for details.
    </div>
  );
}
