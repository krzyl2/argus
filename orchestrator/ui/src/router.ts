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

function normalizeHash(hash: string): string {
  const path = hash.replace(/^#/, '');
  return path || '/sensors'; // root with no hash -> default route (client-side equiv. of v3 302)
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

window.addEventListener('hashchange', () => {
  const path = normalizeHash(location.hash);
  route.value = path;
  routeGroupId.value = parseGroupId(path);
});

// On boot, if there is no hash at all, set one (client-side equivalent of the
// v3.0 server 302 redirect from GET / -> /sensors).
effect(() => {
  if (!location.hash) {
    location.hash = '#/sensors';
  }
});
