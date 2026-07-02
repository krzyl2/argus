import { signal, effect } from '@preact/signals';

// Hand-rolled hash router: this phase ships exactly one route (#/sensors).
// Do NOT add a router library (preact-router/preact-iso) — see RESEARCH Pattern 2.
export const route = signal(normalizeHash(location.hash));

function normalizeHash(hash: string): string {
  const path = hash.replace(/^#/, '');
  return path || '/sensors'; // root with no hash -> default route (client-side equiv. of v3 302)
}

window.addEventListener('hashchange', () => {
  route.value = normalizeHash(location.hash);
});

// On boot, if there is no hash at all, set one (client-side equivalent of the
// v3.0 server 302 redirect from GET / -> /sensors).
effect(() => {
  if (!location.hash) {
    location.hash = '#/sensors';
  }
});
