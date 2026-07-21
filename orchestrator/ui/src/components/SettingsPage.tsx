import { useEffect } from 'preact/hooks';
import { settings, loadError, loadSettings } from '../state/settings';
import { theme, setTheme, type Theme } from '../state/theme';
import {
  includePatterns,
  excludePatterns,
  saveState,
  loadSensors,
  save,
} from '../state/sensors';
import { Card } from './Card';
import { Badge } from './Badge';
import { Banner } from './Banner';
import { Input } from './Input';
import { Select } from './Select';
import { PatternFiltersPanel } from './PatternFiltersPanel';
import { SaveBar } from './SaveBar';
import { SaveResultBanner } from './SaveResultBanner';

const THEME_OPTIONS: { value: Theme; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

// Values MUST match what GET /api/settings emits for logLevel — the orchestrator
// surfaces config["Logging:LogLevel:Default"], which 10-config-gen.sh sets to the
// .NET-cased "Debug" / "Information" / "Warning". Lowercase values never match the
// live value, so the read-only <Select> would render blank on every deployment.
const LOG_LEVEL_OPTIONS = [
  { value: 'Debug', label: 'Debug' },
  { value: 'Information', label: 'Information' },
  { value: 'Warning', label: 'Warning' },
];

const LOG_LEVEL_UNSET_OPTIONS = [{ value: '', label: '—' }];

// noop onChange — every control in Connections/Batch & detection is disabled
// (D-06/D-08, read-only), so Input/Select never actually fire a change.
function noop(): void {}

// Settings screen (#/settings). Connections + Batch & detection are read-only,
// driven live by GET /api/settings (Plan 11-01). Appearance is the one functional
// section (D-09) — a second surface over the shared state/theme.ts signal, in sync
// with the Sidebar toggle.
export function SettingsPage() {
  useEffect(() => {
    void loadSettings();
    // D-07 (Pitfall 1, CRITICAL): this section's Save reuses state/sensors.ts's
    // full-list-replace save() — load the FULL tracked-sensor set on mount so a
    // pattern-filter-only edit here can never silently untrack every other sensor.
    loadSensors('');
  }, []);

  const s = settings.value;
  const patternsSaving = saveState.value === 'saving';
  const patternsResult = typeof saveState.value === 'object' ? saveState.value.result : null;

  return (
    <>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">Settings</h1>
        <p class="argus-page-header__subtitle">
          Current connection and detection configuration. Appearance can be changed below; other
          values are read-only.
        </p>
      </header>

      {loadError.value && (
        <Banner tone="error">Couldn't load settings. Refresh to try again.</Banner>
      )}

      <div class="argus-settings-layout">
        <section>
          <h2 class="argus-section-label">Connections</h2>
          <Card padding="sm">
            <p class="argus-label">
              Home Assistant &amp; MQTT are injected automatically by the Supervisor.{' '}
              <Badge tone="ok">auto</Badge>
            </p>

            <div class="argus-param-field">
              <span class="argus-param-field__label">Detector gRPC endpoint (mTLS)</span>
              <Input
                value={s?.detectorEndpoint ?? ''}
                onChange={noop}
                disabled
                ariaLabel="Detector gRPC endpoint (mTLS)"
              />
            </div>

            <div class="argus-param-grid">
              <div class="argus-param-field">
                <span class="argus-param-field__label">InfluxDB URL (optional)</span>
                <Input
                  value={s?.influxUrl ?? ''}
                  onChange={noop}
                  disabled
                  ariaLabel="InfluxDB URL (optional)"
                />
              </div>
              <div class="argus-param-field">
                <span class="argus-param-field__label">InfluxDB bucket</span>
                <Input
                  value={s?.influxBucket ?? ''}
                  onChange={noop}
                  disabled
                  ariaLabel="InfluxDB bucket"
                />
              </div>
            </div>
          </Card>
        </section>

        <section>
          <h2 class="argus-section-label">Batch &amp; detection</h2>
          <Card padding="sm">
            <div class="argus-param-grid">
              <div class="argus-param-field">
                <span class="argus-param-field__label">Batch interval (minutes)</span>
                <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-xs)' }}>
                  <Input
                    value={s ? String(s.batchIntervalMinutes) : ''}
                    onChange={noop}
                    type="number"
                    disabled
                    ariaLabel="Batch interval (minutes)"
                  />
                  <span class="argus-label">min</span>
                </div>
              </div>
              <div class="argus-param-field">
                <span class="argus-param-field__label">Nightly fit hour (UTC)</span>
                <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-xs)' }}>
                  <Input
                    value={s ? String(s.nightlyFitHour) : ''}
                    onChange={noop}
                    type="number"
                    disabled
                    ariaLabel="Nightly fit hour (UTC)"
                  />
                  <span class="argus-label">h</span>
                </div>
              </div>
            </div>

            <div class="argus-param-field">
              <span class="argus-param-field__label">Log level</span>
              <Select
                value={s?.logLevel ?? ''}
                onChange={noop}
                disabled
                ariaLabel="Log level"
                options={s?.logLevel ? LOG_LEVEL_OPTIONS : LOG_LEVEL_UNSET_OPTIONS}
              />
            </div>
          </Card>
        </section>

        <section>
          <h2 class="argus-section-label">Appearance</h2>
          <Card padding="sm">
            <span class="argus-label">Theme</span>
            <div class="argus-sensitivity-preset-picker">
              <div
                class="argus-sensitivity-preset-picker__options"
                role="radiogroup"
                aria-label="Theme"
              >
                {THEME_OPTIONS.map((option) => (
                  <label key={option.value} class="argus-sensitivity-preset-picker__option">
                    <input
                      type="radio"
                      name="theme"
                      value={option.value}
                      checked={theme.value === option.value}
                      onChange={() => setTheme(option.value)}
                    />
                    <span class="argus-label">{option.label}</span>
                  </label>
                ))}
              </div>
            </div>
          </Card>
        </section>

        <section>
          <h2 class="argus-section-label">Auto-track patterns</h2>
          <Card padding="sm">
            <PatternFiltersPanel
              include={includePatterns.value}
              exclude={excludePatterns.value}
              onIncludeChange={(v) => (includePatterns.value = v)}
              onExcludeChange={(v) => (excludePatterns.value = v)}
            />
            <SaveBar saving={patternsSaving} disabled={patternsSaving} onSave={save} />
            {patternsResult && <SaveResultBanner result={patternsResult} />}
          </Card>
        </section>
      </div>
    </>
  );
}
