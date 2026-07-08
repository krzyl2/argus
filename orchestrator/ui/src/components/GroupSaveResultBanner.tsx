import type { GroupSaveResponse } from '../api/types';
import { Banner } from './Banner';

interface GroupSaveResultBannerProps {
  result: GroupSaveResponse;
  memberCount: number;
}

// Group-editor analog of SaveResultBanner — reuses the exact ok/kind discriminant
// branching logic and banner classes, with group-specific copy (08-UI-SPEC.md
// Copywriting Contract: "Saved — group active. {memberCount} member(s) tracked.").
// A separate component (not a shared one) because the success copy/fields differ
// from the sensor save response (no hasHst/warm-up note for groups).
export function GroupSaveResultBanner({ result, memberCount }: GroupSaveResultBannerProps) {
  if (result.ok) {
    const memberWord = memberCount === 1 ? 'member' : 'members';
    return (
      <Banner tone="success">
        Saved — group active. {memberCount} {memberWord} tracked.
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
