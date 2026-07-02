interface AttributionBarProps {
  memberId: string;
  contribution: number;
  topContribution: number;
  topRank: boolean;
}

// One ranked row: member/feature name + a CSS div-width bar sized by contribution % of the
// top contributor + numeric value. No chart/icon library (08-UI-SPEC.md Attribution Display
// Contract #2) — top-ranked row uses --color-accent fill, all others use a neutral fill,
// since accent is reserved for "the one answer".
export function AttributionBar({ memberId, contribution, topContribution, topRank }: AttributionBarProps) {
  const widthPct = topContribution > 0 ? Math.min(100, (contribution / topContribution) * 100) : 0;

  return (
    <div class="argus-attribution-bar">
      <span class="argus-label argus-attribution-bar__label">{memberId}</span>
      <div class="argus-attribution-bar__track">
        <div
          class={`argus-attribution-bar__fill${topRank ? ' argus-attribution-bar__fill--top' : ''}`}
          style={{ width: `${widthPct}%` }}
        />
      </div>
      <span class="argus-label argus-attribution-bar__value">{contribution.toFixed(3)}</span>
    </div>
  );
}
