import { useEffect } from 'preact/hooks';
import { trackedCount, groupCount, loadError, health, recentAnomalies, loadDashboard } from '../state/dashboard';
import type { HealthComponent, RecentAnomaly } from '../api/types';
import { KpiTile } from './KpiTile';
import { Banner } from './Banner';
import { Card } from './Card';
import { StatusDot } from './StatusDot';
import { Badge } from './Badge';

type Severity = 'high' | 'med' | 'low';

function severityToStatus(severity: Severity): 'ok' | 'warn' | 'error' | 'idle' {
  if (severity === 'high') return 'error';
  if (severity === 'med') return 'warn';
  return 'idle';
}

function severityToBadgeTone(severity: Severity): 'error' | 'warn' | 'neutral' {
  if (severity === 'high') return 'error';
  if (severity === 'med') return 'warn';
  return 'neutral';
}

function scoreToSeverity(score: number): Severity {
  if (score >= 0.8) return 'high';
  if (score >= 0.5) return 'med';
  return 'low';
}

function formatRelative(iso: string): string {
  const deltaMs = Date.now() - new Date(iso).getTime();
  const deltaSec = deltaMs / 1000;
  if (deltaSec < 60) return 'just now';
  const deltaMin = deltaSec / 60;
  if (deltaMin < 60) return `${Math.round(deltaMin)} min ago`;
  const deltaHr = deltaMin / 60;
  if (deltaHr < 24) return `${Math.round(deltaHr)} hr ago`;
  const deltaDay = deltaHr / 24;
  return `${Math.round(deltaDay)} d ago`;
}

function renderAnomalyRow(a: RecentAnomaly) {
  const severity = scoreToSeverity(a.score);
  return (
    <div class="argus-list-row" key={`${a.entityId ?? a.groupId}-${a.detectedAtUtc}`}>
      <StatusDot status={severityToStatus(severity)} />
      <div class="argus-row-content">
        <span class="argus-row-entity-id" style={{ fontFamily: 'var(--font-mono)' }}>
          {a.entityId ?? a.groupId}
        </span>
        <span class="argus-row-friendly-name">
          {a.entityId ? 'sensor' : 'group'} · {a.detector} · {formatRelative(a.detectedAtUtc)}
        </span>
      </div>
      <div class="argus-row-meta">
        <Badge tone={severityToBadgeTone(severity)}>{a.score.toFixed(2)}</Badge>
      </div>
    </div>
  );
}

function renderHealthRow(h: HealthComponent) {
  return (
    <div class="argus-list-row" key={h.key}>
      <StatusDot status={h.status} />
      <div class="argus-row-content">
        <span class="argus-row-entity-id">{h.label}</span>
        <span class="argus-row-friendly-name">{h.detail}</span>
      </div>
    </div>
  );
}

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
        <KpiTile
          label="Home Assistant"
          value={health.value ? (health.value.homeAssistant.connected ? 'Connected' : 'Disconnected') : '—'}
          hint={health.value ? `${health.value.homeAssistant.entityCount} entities` : undefined}
        />
      </div>

      <div class="argus-dashboard-layout">
        <div>
          <p class="argus-section-label">Recent anomalies</p>
          <Card padding="none">
            {recentAnomalies.value === null ? (
              <div class="argus-list-row">
                <span class="argus-row-friendly-name">Couldn't load recent anomalies.</span>
              </div>
            ) : recentAnomalies.value.length === 0 ? (
              <div class="argus-list-row">
                <span class="argus-row-friendly-name">No recent anomalies.</span>
              </div>
            ) : (
              recentAnomalies.value.map(renderAnomalyRow)
            )}
          </Card>
        </div>

        <div>
          <p class="argus-section-label">System health</p>
          <Card padding="sm">
            {health.value === null ? (
              <div class="argus-list-row">
                <span class="argus-row-friendly-name">Health status unavailable.</span>
              </div>
            ) : (
              health.value.components.map(renderHealthRow)
            )}
          </Card>
        </div>
      </div>
    </div>
  );
}
