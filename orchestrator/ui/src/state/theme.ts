import { signal } from '@preact/signals';

// Shared theme state (D-09) — single write path so the Sidebar toggle and the
// Settings "Appearance" radio group (Phase 11 11-05) stay in sync. Mirrors
// the signals-module convention in state/groups.ts. Light/Dark only, no
// 'system' option, no new persistence key — same 'argus-theme' localStorage
// key + data-theme attribute mechanism established in Phase 10.
export type Theme = 'light' | 'dark';

// Bootstrap logic moved here (was inline in main.tsx) — ES module static
// imports always fully evaluate before the importing module's own top-level
// code runs, so an inline bootstrap left in main.tsx would execute AFTER this
// module is evaluated (via the AppShell -> Sidebar -> theme import chain),
// reading a not-yet-set attribute. Owning bootstrap here guarantees data-theme
// is applied before first paint, with a single source of truth (no double logic).
function resolveInitialTheme(): Theme {
  const stored = localStorage.getItem('argus-theme');
  if (stored === 'light' || stored === 'dark') return stored;
  // jsdom (test environment) does not implement matchMedia — guard so tests
  // don't crash on import; browsers always have it.
  const prefersDark =
    typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
  return prefersDark ? 'dark' : 'light';
}

const initialTheme = resolveInitialTheme();
document.documentElement.setAttribute('data-theme', initialTheme);

export const theme = signal<Theme>(initialTheme);

/** Single write path for theme: sets data-theme, persists to localStorage, updates the signal. */
export function setTheme(next: Theme): void {
  document.documentElement.setAttribute('data-theme', next);
  localStorage.setItem('argus-theme', next);
  theme.value = next;
}
