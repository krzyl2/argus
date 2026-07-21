import { signal, effect } from '@preact/signals';

// Hand-rolled hash router — see 08-UI-SPEC.md "Screens / Routes". Do NOT add a
// router library (preact-router/preact-iso) — see RESEARCH Pattern 2.
//
// Phase 11 (D-10) adds 3 new static routes: /dashboard, /algorithms, /settings.
// None take an :id segment, so they need no parser changes here — they flow
// through the existing `route` signal + hashchange listener like /sensors and
// /groups already do; only Sidebar.tsx (nav+isActive) and main.tsx (render
// switch) needed updates.
export const route = signal(normalizeHash(location.hash));

// Parsed :id segment for #/groups/:id (null for /groups, /groups/new, or /sensors).
export const routeGroupId = signal(parseGroupId(normalizeHash(location.hash)));

// Phase 14 (D-01/D-05): parsed :entityId segment for #/detectors/sensor/:entityId
// (null for any other route). Exported alongside routeGroupId.
export const routeSensorEntityId = signal(parseSensorEntityId(normalizeHash(location.hash)));

// Exported so router.test.ts can assert D-01/D-05 redirect + parse behaviors directly
// (was module-internal before Phase 14).
export function normalizeHash(hash: string): string {
  const path = hash.replace(/^#/, '') || '/detectors'; // root with no hash -> default route (client-side equiv. of v3 302)
  // D-01/D-05: bare legacy routes redirect to the new unified screen. Exact-match
  // only, so /groups/new and /groups/:id keep passing through unchanged.
  if (path === '/sensors' || path === '/groups') return '/detectors';
  return path;
}

/**
 * Extracts the :id segment from a /groups/:id path. Returns null for /groups,
 * /groups/new, or any non-group route — /groups/new is intentionally excluded
 * from id parsing since "new" is a reserved segment, not a group id.
 */
function parseGroupId(path: string): string | null {
  const match = path.match(/^\/groups\/([^/]+)$/);
  if (!match) return null;
  const segment = match[1];
  return segment === 'new' ? null : segment;
}

/**
 * Extracts the :entityId segment from a /detectors/sensor/:entityId path.
 * Entity ids are encodeURIComponent-encoded at link time (they contain dots,
 * e.g. sensor.living_room_temp) — decodeURIComponent here, mirroring
 * parseGroupId's idiom. Returns null for a non-matching path or a malformed
 * percent-encoding (defensive fallback per T-14-01-01).
 */
export function parseSensorEntityId(path: string): string | null {
  const match = path.match(/^\/detectors\/sensor\/([^/]+)$/);
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return null;
  }
}

window.addEventListener('hashchange', () => {
  const path = normalizeHash(location.hash);
  route.value = path;
  routeGroupId.value = parseGroupId(path);
  routeSensorEntityId.value = parseSensorEntityId(path);
});

// On boot, if there is no hash at all, set one (client-side equivalent of the
// v3.0 server 302 redirect from GET / -> /detectors).
effect(() => {
  if (!location.hash) {
    location.hash = '#/detectors';
  }
});
