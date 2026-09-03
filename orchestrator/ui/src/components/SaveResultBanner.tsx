import type { SaveResponse } from '../api/types';
import { Banner } from './Banner';

interface SaveResultBannerProps {
  result: SaveResponse;
}

// Replaces #argus-flash + Build*Banner methods. Branches on ok/kind discriminant,
// never string-sniffing.
//
// The old warm-up note said "HST ... window=250 at ~1 reading/s ... ~4 minutes". After the
// migration all three numbers are wrong: the detector is rmad, the gate is min_samples (60),
// and the measured cadences on real sensors run from 15,3 s to 391 s per reading — so "~4
// minutes" was off by up to two orders of magnitude. A wrong number here is worse than none:
// it tells the operator the system is broken when it is merely still warming up.
export function SaveResultBanner({ result }: SaveResultBannerProps) {
  if (result.ok) {
    const entityWord = result.count === 1 ? 'entity' : 'entities';
    return (
      <Banner tone="success">
        Saved — pipeline active. {result.count} {entityWord} tracked.
        {result.hasStreaming && (
          <p class="argus-warmup-note">
            Streaming detectors need min_samples readings before their first verdict (60 by
            default). How long that takes depends on how often each sensor reports — see the
            per-field note in the detector editor. Anomaly flags stay off until then.
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
