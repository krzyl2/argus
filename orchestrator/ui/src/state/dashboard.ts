import { signal } from '@preact/signals';
import { apiGet } from '../api/client';
import type { SensorsResponse, GroupsResponse } from '../api/types';

// Dashboard KPI signals (DASH-01). `null` means "not loaded yet / load failed" —
// the page renders "—" for null rather than a stale/zero value presented as real.
export const trackedCount = signal<number | null>(null);
export const groupCount = signal<number | null>(null);
export const loadError = signal(false);

/**
 * Fetches the KPI inputs from the existing /api/sensors and /api/groups endpoints
 * (Dashboard is frontend-only per D-01 — no new backend). On any failure, counts
 * are left null (never a fake 0) and loadError is set so the page can render an
 * error Banner.
 */
export async function loadDashboard(): Promise<void> {
  loadError.value = false;
  try {
    const [sensorsRes, groupsRes] = await Promise.all([
      apiGet<SensorsResponse>('api/sensors'),
      apiGet<GroupsResponse>('api/groups'),
    ]);
    trackedCount.value = sensorsRes.entries.filter((e) => e.isTracked).length;
    groupCount.value = groupsRes.groups.length;
  } catch {
    trackedCount.value = null;
    groupCount.value = null;
    loadError.value = true;
  }
}
