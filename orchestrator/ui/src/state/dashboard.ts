import { signal } from '@preact/signals';
import { apiGet } from '../api/client';
import type { SensorsResponse, GroupsResponse, HealthResponse, RecentAnomaliesResponse, RecentAnomaly } from '../api/types';

// Dashboard KPI signals (DASH-01). `null` means "not loaded yet / load failed" —
// the page renders "—" for null rather than a stale/zero value presented as real.
export const trackedCount = signal<number | null>(null);
export const groupCount = signal<number | null>(null);
export const loadError = signal(false);

// QUICK-dashboard-real-data: health + recent anomalies signals. `null` means
// "not loaded yet / load failed" — never a fabricated empty or zero value.
export const health = signal<HealthResponse | null>(null);
export const recentAnomalies = signal<RecentAnomaly[] | null>(null);

/**
 * Fetches the KPI inputs from the existing /api/sensors and /api/groups endpoints.
 * On any failure, counts are left null (never a fake 0) and loadError is set so the
 * page can render an error Banner.
 */
async function loadCounts(): Promise<void> {
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

/** Fetches GET /api/health into the health signal; null on failure. */
async function loadHealth(): Promise<void> {
  try {
    health.value = await apiGet<HealthResponse>('api/health');
  } catch {
    health.value = null;
  }
}

/** Fetches GET /api/anomalies/recent into the recentAnomalies signal; null on failure. */
async function loadRecentAnomalies(): Promise<void> {
  try {
    const res = await apiGet<RecentAnomaliesResponse>('api/anomalies/recent');
    recentAnomalies.value = res.anomalies;
  } catch {
    recentAnomalies.value = null;
  }
}

/**
 * Loads all three Dashboard areas (counts, health, recent anomalies) independently —
 * one failing endpoint must not blank the others. Each loader has its own try/catch;
 * loadError stays scoped to the counts area (its banner text is about counts).
 */
export async function loadDashboard(): Promise<void> {
  await Promise.all([loadCounts(), loadHealth(), loadRecentAnomalies()]);
}
