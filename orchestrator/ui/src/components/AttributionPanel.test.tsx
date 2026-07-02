import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/preact';
import { AttributionPanel } from './AttributionPanel';
import * as client from '../api/client';
import type { GroupStatusResponse } from '../api/types';

describe('AttributionPanel', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('renders ranked bars in received order (ecod/copod) without re-sorting, top bar accented', async () => {
    const res: GroupStatusResponse = {
      status: {
        groupId: 'grp.living_room',
        score: 4.2,
        isAnomaly: true,
        detector: 'ecod',
        scoredAtUtc: '2026-07-02T00:00:00Z',
        contributions: [
          { memberId: 'sensor.b', contribution: 0.6 },
          { memberId: 'sensor.a', contribution: 0.3 },
          { memberId: 'sensor.c', contribution: 0.1 },
        ],
      },
    };
    vi.spyOn(client, 'apiGet').mockResolvedValue(res);

    const { container } = render(<AttributionPanel groupId="grp.living_room" />);

    await waitFor(() => expect(screen.getByText('sensor.b')).toBeTruthy());

    // Received order preserved, not re-sorted by a different key.
    const labels = Array.from(container.querySelectorAll('.argus-attribution-bar__label')).map(
      (el) => el.textContent
    );
    expect(labels).toEqual(['sensor.b', 'sensor.a', 'sensor.c']);

    const bars = container.querySelectorAll('.argus-attribution-bar__fill');
    expect(bars[0].classList.contains('argus-attribution-bar__fill--top')).toBe(true);
    expect(bars[1].classList.contains('argus-attribution-bar__fill--top')).toBe(false);
    expect(bars[2].classList.contains('argus-attribution-bar__fill--top')).toBe(false);
  });

  it('renders the honest no-attribution message for pca/iforest (not an error state)', async () => {
    const res: GroupStatusResponse = {
      status: {
        groupId: 'grp.x',
        score: 1.1,
        isAnomaly: false,
        detector: 'pca',
        scoredAtUtc: '2026-07-02T00:00:00Z',
        contributions: [],
      },
    };
    vi.spyOn(client, 'apiGet').mockResolvedValue(res);

    render(<AttributionPanel groupId="grp.x" />);

    await waitFor(() =>
      expect(screen.getByText('This algorithm does not provide per-feature attribution.')).toBeTruthy()
    );
  });

  it('renders the no-verdict-yet state when status is null', async () => {
    const res: GroupStatusResponse = { status: null };
    vi.spyOn(client, 'apiGet').mockResolvedValue(res);

    render(<AttributionPanel groupId="grp.new" />);

    await waitFor(() =>
      expect(
        screen.getByText('No anomaly score yet — attribution will appear after the next batch run.')
      ).toBeTruthy()
    );
  });

  it('polls on an interval and clears it on unmount (no leak after route change)', async () => {
    const apiGetSpy = vi.spyOn(client, 'apiGet').mockResolvedValue({ status: null });

    const { unmount } = render(<AttributionPanel groupId="grp.x" />);

    await waitFor(() => expect(apiGetSpy).toHaveBeenCalledTimes(1));

    vi.advanceTimersByTime(60_000);
    await Promise.resolve();
    expect(apiGetSpy).toHaveBeenCalledTimes(2);

    unmount();
    vi.advanceTimersByTime(120_000);
    await Promise.resolve();

    // No further calls after unmount.
    expect(apiGetSpy).toHaveBeenCalledTimes(2);
  });

  it('WR-03: URL-encodes groupId in the status poll path', async () => {
    const apiGetSpy = vi.spyOn(client, 'apiGet').mockResolvedValue({ status: null });

    render(<AttributionPanel groupId="grp/weird?id" />);

    await waitFor(() => expect(apiGetSpy).toHaveBeenCalledTimes(1));
    expect(apiGetSpy).toHaveBeenCalledWith('api/groups/grp%2Fweird%3Fid/status');
  });
});
