import { useEffect } from 'preact/hooks';
import { catalog, loadError, loadCatalog } from '../state/algorithms';
import { Card } from './Card';
import { Badge } from './Badge';
import { Disclosure } from './Disclosure';
import { Banner } from './Banner';
import type { DetectorCatalogEntry, DetectorPreset, ParamFieldSchema } from '../api/types';

const MONO_FONT = { fontFamily: 'var(--font-mono)' };

/** "{preset.label}: {params as key=value joined by comma, in entry.paramSchema key order}" */
function formatPresetBadge(preset: DetectorPreset, paramSchema: ParamFieldSchema[]): string {
  const pairs = paramSchema
    .filter((field) => field.key in preset.params)
    .map((field) => `${field.key}=${preset.params[field.key]}`);
  return `${preset.label}: ${pairs.join(', ')}`;
}

/** "{type} · {min}–{max} · step {step}" */
function formatParamRange(field: ParamFieldSchema): string {
  const min = field.min ?? '—';
  const max = field.max ?? '—';
  const step = field.step ?? '—';
  return `${field.type} · ${min}–${max} · step ${step}`;
}

// One read-only detector card (D-04/D-05: no SensitivityPreset picker, no Input — this is
// browse-only, distinct from AlgorithmChooser's editable wizard cards).
function AlgorithmCatalogCard({ entry }: { entry: DetectorCatalogEntry }) {
  return (
    <Card padding="sm">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-sm)' }}>
        <span
          class="argus-body"
          style={{
            ...MONO_FONT,
            fontSize: 'var(--font-size-lead)',
            fontWeight: 'var(--font-weight-semibold)',
          }}
        >
          {entry.name}
        </span>
        {/* Best-for copy rendered verbatim from the catalog API — never hardcoded/paraphrased
            client-side (Phase 9's own anti-pattern lesson). */}
        <p class="argus-body" style={{ color: 'var(--color-text-secondary)', lineHeight: 1.5, margin: 0 }}>
          {entry.bestFor}
        </p>
        <div>
          <span class="argus-label">Presets</span>
          <div
            style={{ display: 'flex', gap: 'var(--space-sm)', flexWrap: 'wrap', marginTop: 'var(--space-xs)' }}
          >
            {entry.presets.map((preset) => (
              <Badge key={preset.label} tone="neutral">
                {formatPresetBadge(preset, entry.paramSchema)}
              </Badge>
            ))}
          </div>
        </div>
        <Disclosure summary="Parameter schema">
          {entry.paramSchema.map((field) => (
            <div key={field.key} class="argus-catalog-param-row">
              <span class="argus-body" style={MONO_FONT}>
                {field.key}
              </span>
              <span class="argus-label">{formatParamRange(field)}</span>
            </div>
          ))}
        </Disclosure>
      </div>
    </Card>
  );
}

// Algorithms screen (#/algorithms, ALGO-07/08). Read-only catalog of the 5 group detectors
// sourced entirely from GET /api/detectors/catalog, in catalog order — never
// reordered/filtered client-side. No SaveBar, no editable controls, no "Single sensors"
// section (rejected per D-04/D-05); distinct from the in-flow AlgorithmChooser wizard used
// when creating/editing a group.
export function AlgorithmsPage() {
  useEffect(() => {
    loadCatalog();
  }, []);

  return (
    <>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">Algorithms</h1>
        <p class="argus-page-header__subtitle">
          Browse the detector catalog used for group anomaly detection.
        </p>
      </header>

      {loadError.value && (
        <Banner tone="error">Couldn't load the detector catalog. Refresh to try again.</Banner>
      )}

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
          gap: 'var(--space-md)',
        }}
      >
        {catalog.value.map((entry) => (
          <AlgorithmCatalogCard key={entry.name} entry={entry} />
        ))}
      </div>
    </>
  );
}
