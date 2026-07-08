import type { SaveResponse } from '../api/types';
import { Banner } from './Banner';

interface SaveResultBannerProps {
  result: SaveResponse;
}

// Replaces #argus-flash + Build*Banner methods. Branches on ok/kind discriminant,
// never string-sniffing. Copy verbatim from EntityPickerPage.cs.
export function SaveResultBanner({ result }: SaveResultBannerProps) {
  if (result.ok) {
    const entityWord = result.count === 1 ? 'entity' : 'entities';
    return (
      <Banner tone="success">
        Saved — pipeline active. {result.count} {entityWord} tracked.
        {result.hasHst && (
          <p class="argus-warmup-note">
            HST detectors need ~4 minutes of readings to warm up (window=250 at ~1 reading/s).
            Anomaly scores will be low until warm-up completes.
          </p>
        )}
      </Banner>
    );
  }

  if (result.kind === 'validation') {
    return (
      <Banner tone="validation">
        Save blocked: {result.errorCount} field(s) have invalid values. Correct the highlighted
        fields and try again.
      </Banner>
    );
  }

  return (
    <Banner tone="error">
      Save failed. {result.reason}. Check the add-on log for details.
    </Banner>
  );
}
