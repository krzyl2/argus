import { useEffect } from 'preact/hooks';
import { trackedCount, groupCount, loadError, loadDashboard } from '../state/dashboard';
import { KpiTile } from './KpiTile';
import { Banner } from './Banner';

// Dashboard screen (#/dashboard). KPI row is real data (DASH-01); "Recent anomalies"
// and "System health" sections are fleshed out in Task 2.
export function DashboardPage() {
  useEffect(() => {
    loadDashboard();
  }, []);

  return (
    <div>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">Dashboard</h1>
        <p class="argus-page-header__subtitle">System overview for your self-hosted Argus instance.</p>
      </header>

      {loadError.value && (
        <Banner tone="error">Couldn't load sensor/group counts. Refresh to try again.</Banner>
      )}

      <div class="argus-dashboard-kpi-row">
        <KpiTile
          label="Monitored sensors"
          value={trackedCount.value ?? '—'}
          accent
        />
        <KpiTile label="Groups" value={groupCount.value ?? '—'} />
        <KpiTile
          label="Active group detectors"
          value={groupCount.value ?? '—'}
          hint="group detectors"
        />
        <KpiTile label="Home Assistant" value="Connected" hint="mocked — no endpoint yet" />
      </div>
    </div>
  );
}
