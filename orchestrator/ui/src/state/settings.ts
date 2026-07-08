import { signal } from '@preact/signals';
import { apiGet } from '../api/client';
import type { SettingsResponse } from '../api/types';

// Settings screen data (D-06/D-08) — mirrors the state/groups.ts signals-module
// convention. Single-shot load on mount; on failure `loadError` is set and
// `settings` stays null so SettingsPage never renders a fabricated value.
export const settings = signal<SettingsResponse | null>(null);
export const loadError = signal(false);

export async function loadSettings(): Promise<void> {
  try {
    const res = await apiGet<SettingsResponse>('api/settings');
    settings.value = res;
    loadError.value = false;
  } catch {
    settings.value = null;
    loadError.value = true;
  }
}
