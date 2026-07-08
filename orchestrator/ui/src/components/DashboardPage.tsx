import { useEffect } from 'preact/hooks';
import { trackedCount, groupCount, loadError, loadDashboard } from '../state/dashboard';
import { KpiTile } from './KpiTile';
import { Banner } from './Banner';
import { Card } from './Card';
import { StatusDot } from './StatusDot';
import { Badge } from './Badge';

type Severity = 'high' | 'med' | 'low';

interface MockAnomaly {
  entity: string | null;
  group: string | null;
  score: number;
  severity: Severity;
  when: string;
  detector: string;
}

interface MockHealthItem {
  label: string;
  status: 'ok' | 'warn' | 'idle';
  detail: string;
}

// D-02: "Recent anomalies" mock+TODO dataset — no anomaly-history endpoint exists yet.
// Exact dataset from 11-UI-SPEC.md's "Recent anomalies" section.
const MOCK_ANOMALIES: MockAnomaly[] = [
  { entity: 'sensor.lazienka_wilgotnosc', group: null, score: 0.91, severity: 'high', when: '2 min ago', detector: 'hst' },
  { entity: null, group: 'Klimat salonu', score: 0.74, severity: 'med', when: '18 min ago', detector: 'copod' },
  { entity: 'sensor.sypialnia_temperatura', group: null, score: 0.68, severity: 'med', when: '1 hr ago', detector: 'mad' },
  { entity: 'sensor.cisnienie_atmosferyczne', group: null, score: 0.55, severity: 'low', when: '3 hr ago', detector: 'stl' },
  { entity: null, group: 'Temperatury pokoi', score: 0.49, severity: 'low', when: '5 hr ago', detector: 'peer_divergence' },
];

// D-03: "System health" mock+TODO dataset — no /api/health endpoint exists yet.
// Exact dataset from 11-UI-SPEC.md's "System health" section.
const MOCK_HEALTH: MockHealthItem[] = [
  { label: 'Home Assistant (WebSocket)', status: 'ok', detail: 'Connected · 412 entities' },
  { label: 'Detector (gRPC, mTLS)', status: 'ok', detail: 'gpu-host:50051 · serving' },
  { label: 'MQTT broker', status: 'ok', detail: 'core_mosquitto · connected' },
  { label: 'Last batch run', status: 'warn', detail: 'Overdue by 4 min (interval 10 min)' },
  { label: 'InfluxDB', status: 'idle', detail: 'Not configured — streaming-only' },
];

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

      <div class="argus-dashboard-layout">
        <div>
          <p class="argus-section-label">Recent anomalies</p>
          <Banner tone="info">
            Mocked — no anomaly-history endpoint yet. Showing example data.
          </Banner>
          <Card padding="none">
            {MOCK_ANOMALIES.map((a) => (
              <div class="argus-list-row" key={`${a.entity ?? a.group}-${a.when}`}>
                <StatusDot status={severityToStatus(a.severity)} />
                <div class="argus-row-content">
                  <span class="argus-row-entity-id" style={{ fontFamily: 'var(--font-mono)' }}>
                    {a.entity ?? a.group}
                  </span>
                  <span class="argus-row-friendly-name">
                    {a.entity ? 'sensor' : 'group'} · {a.detector} · {a.when}
                  </span>
                </div>
                <div class="argus-row-meta">
                  <Badge tone={severityToBadgeTone(a.severity)}>{a.score.toFixed(2)}</Badge>
                </div>
              </div>
            ))}
          </Card>
        </div>

        <div>
          <p class="argus-section-label">System health</p>
          <Banner tone="info">Mocked — no /api/health endpoint yet. Showing example data.</Banner>
          <Card padding="sm">
            {MOCK_HEALTH.map((h) => (
              <div class="argus-list-row" key={h.label}>
                <StatusDot status={h.status} />
                <div class="argus-row-content">
                  <span class="argus-row-entity-id">{h.label}</span>
                  <span class="argus-row-friendly-name">{h.detail}</span>
                </div>
              </div>
            ))}
          </Card>
        </div>
      </div>
    </div>
  );
}
