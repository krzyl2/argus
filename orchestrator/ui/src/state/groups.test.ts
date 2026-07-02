import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  loadGroups,
  groups,
  loading,
  saveGroup,
  saveState,
  draftGroupId,
  draftFriendlyName,
  draftMembers,
  draftMode,
  draftDetector,
  draftParams,
} from './groups';
import * as client from '../api/client';
import type { GroupConfig } from '../api/types';

function makeGroup(overrides: Partial<GroupConfig>): GroupConfig {
  return {
    groupId: 'grp.x',
    friendlyName: 'X',
    members: ['sensor.a', 'sensor.b', 'sensor.c'],
    mode: 'peer_divergence',
    detector: 'peer_divergence',
    params: {},
    ...overrides,
  };
}

describe('loadGroups', () => {
  beforeEach(() => {
    groups.value = [];
    loading.value = false;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('populates groups on a single successful call', async () => {
    vi.spyOn(client, 'apiGet').mockResolvedValue({ groups: [makeGroup({ groupId: 'grp.a' })] });

    await loadGroups();

    expect(groups.value).toHaveLength(1);
    expect(groups.value[0].groupId).toBe('grp.a');
    expect(loading.value).toBe(false);
  });

  it('ignores a stale response that resolves after a newer request (out-of-order race)', async () => {
    let resolveFirst!: (v: { groups: GroupConfig[] }) => void;
    const staleGroups = [makeGroup({ groupId: 'grp.stale' })];
    const freshGroups = [makeGroup({ groupId: 'grp.fresh' })];

    const apiGetSpy = vi.spyOn(client, 'apiGet');
    apiGetSpy.mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve; }));
    apiGetSpy.mockImplementationOnce(() => Promise.resolve({ groups: freshGroups }));

    const firstCall = loadGroups();
    const secondCall = loadGroups();

    await secondCall;
    expect(groups.value).toEqual(freshGroups);

    resolveFirst({ groups: staleGroups });
    await firstCall;

    expect(groups.value).toEqual(freshGroups);
  });
});

describe('saveGroup', () => {
  beforeEach(() => {
    groups.value = [];
    saveState.value = 'idle';
    draftGroupId.value = 'grp.new';
    draftFriendlyName.value = 'New Group';
    draftMembers.value = ['sensor.a', 'sensor.b', 'sensor.c'];
    draftMode.value = 'joint';
    draftDetector.value = null;
    draftParams.value = {};
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('CR-02: refuses to save and does not POST when no algorithm was chosen', async () => {
    const apiPostSpy = vi.spyOn(client, 'apiPost');

    await saveGroup();

    expect(apiPostSpy).not.toHaveBeenCalled();
    expect(saveState.value).toEqual({
      result: { ok: false, kind: 'error', reason: 'Choose an algorithm to continue.' },
    });
  });

  it('CR-02: saves with the explicitly chosen detector (no silent peer_divergence default)', async () => {
    draftDetector.value = 'ecod';
    const apiPostSpy = vi.spyOn(client, 'apiPost').mockResolvedValue({ ok: true, count: 1 });
    vi.spyOn(client, 'apiGet').mockResolvedValue({ groups: [] });

    await saveGroup();

    const postedBody = apiPostSpy.mock.calls[0][1] as { groups: GroupConfig[] };
    expect(postedBody.groups[0].detector).toBe('ecod');
  });
});

describe('deleteGroup', () => {
  beforeEach(() => {
    groups.value = [
      makeGroup({ groupId: 'grp.a' }),
      makeGroup({ groupId: 'grp.b' }),
      makeGroup({ groupId: 'grp.c' }),
    ];
    loading.value = false;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('posts the full groups list minus the removed groupId to api/groups/save', async () => {
    const apiPostSpy = vi
      .spyOn(client, 'apiPost')
      .mockResolvedValue({ ok: true, count: 2 });
    vi.spyOn(client, 'apiGet').mockResolvedValue({ groups: [] });

    const { deleteGroup } = await import('./groups');
    await deleteGroup('grp.b');

    expect(apiPostSpy).toHaveBeenCalledWith(
      'api/groups/save',
      expect.objectContaining({
        groups: expect.arrayContaining([
          expect.objectContaining({ groupId: 'grp.a' }),
          expect.objectContaining({ groupId: 'grp.c' }),
        ]),
      })
    );
    const postedBody = apiPostSpy.mock.calls[0][1] as { groups: GroupConfig[] };
    expect(postedBody.groups.map((g) => g.groupId)).not.toContain('grp.b');
    expect(postedBody.groups).toHaveLength(2);
  });

  it('posts the unchanged list when the targeted id does not exist (no crash)', async () => {
    const apiPostSpy = vi
      .spyOn(client, 'apiPost')
      .mockResolvedValue({ ok: true, count: 3 });
    vi.spyOn(client, 'apiGet').mockResolvedValue({ groups: [] });

    const { deleteGroup } = await import('./groups');
    await deleteGroup('grp.unknown');

    const postedBody = apiPostSpy.mock.calls[0][1] as { groups: GroupConfig[] };
    expect(postedBody.groups).toHaveLength(3);
  });
});
