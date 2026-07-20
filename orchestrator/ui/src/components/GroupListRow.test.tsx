import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/preact';
import { GroupListRow } from './GroupListRow';
import * as groupsState from '../state/groups';
import type { GroupConfig, GroupStatus } from '../api/types';

function makeGroup(overrides: Partial<GroupConfig> = {}): GroupConfig {
  return {
    groupId: 'living_room',
    friendlyName: 'Living Room',
    members: ['sensor.a', 'sensor.b'],
    mode: 'peer_divergence',
    detector: 'peer_divergence',
    params: {},
    ...overrides,
  };
}

function makeStatus(overrides: Partial<GroupStatus> = {}): GroupStatus {
  return {
    groupId: 'living_room',
    score: 0.1,
    isAnomaly: false,
    contributions: [],
    detector: 'peer_divergence',
    scoredAtUtc: '2026-07-20T00:00:00Z',
    ...overrides,
  };
}

describe('GroupListRow', () => {
  beforeEach(() => {
    vi.spyOn(groupsState, 'deleteGroup').mockResolvedValue(undefined);
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('renders both a mode Badge and a detector Badge', () => {
    render(<GroupListRow group={makeGroup()} />);
    expect(screen.getByText('peer')).toBeTruthy();
    expect(screen.getByText('peer_divergence')).toBeTruthy();
  });

  it('renders "no status yet" when status is absent', () => {
    render(<GroupListRow group={makeGroup()} />);
    expect(screen.getByText('no status yet')).toBeTruthy();
  });

  it('renders a status Badge when status is present', () => {
    render(<GroupListRow group={makeGroup()} status={makeStatus({ isAnomaly: true })} />);
    expect(screen.getByText('anomaly')).toBeTruthy();
    expect(screen.queryByText('no status yet')).toBeNull();
  });

  it('arms on first delete click, showing the confirm label', () => {
    render(<GroupListRow group={makeGroup()} />);
    const btn = screen.getByText('Delete group');
    fireEvent.click(btn);
    expect(screen.getByText('Confirm delete')).toBeTruthy();
    expect(groupsState.deleteGroup).not.toHaveBeenCalled();
  });

  it('fires deleteGroup on a second click within the confirm window', () => {
    render(<GroupListRow group={makeGroup()} />);
    fireEvent.click(screen.getByText('Delete group'));
    fireEvent.click(screen.getByText('Confirm delete'));
    expect(groupsState.deleteGroup).toHaveBeenCalledWith('living_room');
  });

  it('reverts to "Delete group" after the confirm window elapses without a second click', () => {
    render(<GroupListRow group={makeGroup()} />);
    fireEvent.click(screen.getByText('Delete group'));
    expect(screen.getByText('Confirm delete')).toBeTruthy();
    act(() => {
      vi.advanceTimersByTime(3001);
    });
    expect(screen.getByText('Delete group')).toBeTruthy();
    expect(groupsState.deleteGroup).not.toHaveBeenCalled();
  });
});
