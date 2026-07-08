import { StatusDot } from './StatusDot';

export interface KpiTileProps {
  label: string;
  value: string | number;
  unit?: string;
  accent?: boolean;
  status?: 'ok' | 'warn' | 'error';
  hint?: string;
}

// Dashboard KPI tile over .argus-kpi-tile family. Not consumed until Phase 11 (Dashboard) but
// built now per Plan 10-03's artifact spec. When `status` is set, a StatusDot renders in place
// of the numeric value (e.g. HA connection tile) — otherwise the tabular-numeric value + unit.
export function KpiTile({ label, value, unit, accent = false, status, hint }: KpiTileProps) {
  return (
    <div class={`argus-kpi-tile${accent ? ' argus-kpi-tile--accent' : ''}`}>
      <span class="argus-kpi-tile__label">{label}</span>
      {status ? (
        <StatusDot status={status} />
      ) : (
        <>
          <span class="argus-kpi-tile__value">{value}</span>
          {unit && <span class="argus-kpi-tile__unit">{unit}</span>}
        </>
      )}
      {hint && <span class="argus-kpi-tile__hint">{hint}</span>}
    </div>
  );
}
