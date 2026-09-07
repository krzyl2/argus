import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { GroupEditorForm } from './GroupEditorForm';
import * as client from '../api/client';
import { groups, draftFriendlyName, draftGroupId, draftMembers, saveState } from '../state/groups';
import * as groupsState from '../state/groups';
import type { SensorEntry, GroupConfig } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.default',
    friendlyName: null,
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

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

describe('GroupEditorForm', () => {
  beforeEach(() => {
    groups.value = [makeGroup()];
    // AlgorithmChooser fetches the catalog on mount — stub it out so it doesn't matter here.
    vi.spyOn(client, 'apiGet').mockResolvedValue({ detectors: [], guided: [] });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders "Create group" in the page-header when groupId is null', () => {
    render(<GroupEditorForm groupId={null} sensors={[]} />);
    expect(screen.getByText('Create group')).toBeTruthy();
    expect(document.querySelector('.argus-page-header')).not.toBeNull();
  });

  it('renders "Edit group" in the page-header when groupId is set', () => {
    render(<GroupEditorForm groupId="living_room" sensors={[]} />);
    expect(screen.getByText('Edit group')).toBeTruthy();
  });

  it('renders a "Back to groups" affordance that sets location.hash to #/groups', () => {
    render(<GroupEditorForm groupId={null} sensors={[]} />);
    const back = screen.getByText('Back to groups');
    (back as HTMLButtonElement).click();
    expect(location.hash).toBe('#/groups');
  });

  it('renders the name field via the shared Input, with the required-name error when empty', () => {
    render(<GroupEditorForm groupId={null} sensors={[]} />);
    const input = document.getElementById('group-name') as HTMLInputElement;
    expect(input).not.toBeNull();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('Must provide a value.')).toBeTruthy();
  });

  it('slugifies the name into draftGroupId on a new group', () => {
    render(<GroupEditorForm groupId={null} sensors={[]} />);
    const input = document.getElementById('group-name') as HTMLInputElement;
    input.value = 'Living Room 2';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    expect(draftFriendlyName.value).toBe('Living Room 2');
    expect(draftGroupId.value).toBe('living_room_2');
  });

  it('renders the mode field via the shared Select', () => {
    render(<GroupEditorForm groupId={null} sensors={[]} />);
    const select = document.querySelector('.argus-detector-select');
    expect(select).not.toBeNull();
    expect(select?.querySelectorAll('option').length).toBe(2);
  });

  it('composes the member picker, algorithm chooser slot, and save bar', () => {
    render(<GroupEditorForm groupId={null} sensors={[makeSensor()]} />);
    expect(document.querySelector('.argus-member-picker')).not.toBeNull();
    expect(document.getElementById('algorithm-chooser-slot')).not.toBeNull();
    expect(document.querySelector('.argus-btn--primary')).not.toBeNull();
  });

  // Intent: the operator must be able to see which sensors belong to the group without
  // first typing a search query into the MemberPicker (the bug this task fixes).
  it('shows all current members in the editor without searching', () => {
    render(
      <GroupEditorForm
        groupId="living_room"
        sensors={[
          makeSensor({ entityId: 'sensor.a' }),
          makeSensor({ entityId: 'sensor.b' }),
        ]}
      />
    );
    // Header reflects the member count.
    expect(screen.getByText('Selected (2)')).toBeTruthy();
    // Both member entity ids are visible although no search term was ever entered.
    expect(screen.getByText('sensor.a')).toBeTruthy();
    expect(screen.getByText('sensor.b')).toBeTruthy();
  });

  it('does not render the selected-members section when the group has no members', () => {
    groups.value = [makeGroup({ members: [] })];
    render(<GroupEditorForm groupId="living_room" sensors={[makeSensor()]} />);
    expect(screen.queryByText(/^Selected \(/)).toBeNull();
  });

  // Intent: the Remove control must drop that member from the draft (via toggleMember),
  // not merely hide it visually.
  it('Remove drops the member from draftMembers via toggleMember', () => {
    render(
      <GroupEditorForm
        groupId="living_room"
        sensors={[
          makeSensor({ entityId: 'sensor.a' }),
          makeSensor({ entityId: 'sensor.b' }),
        ]}
      />
    );
    expect(draftMembers.value).toEqual(['sensor.a', 'sensor.b']);
    const removeButtons = screen.getAllByText('Remove');
    expect(removeButtons).toHaveLength(2);
    // First Remove corresponds to sensor.a (list order mirrors selectedMembers).
    (removeButtons[0] as HTMLButtonElement).click();
    expect(draftMembers.value).toEqual(['sensor.b']);
  });

  // Group delete lives here because Phase 14's /groups -> /detectors redirect made
  // GroupListRow (the old home of this affordance) unreachable. The editor is now the
  // only screen from which an operator can retire a group.
  describe('delete group', () => {
    beforeEach(() => {
      saveState.value = 'idle';
    });

    // Intent: /groups/new has no server-side group to retire, and deleteGroup posts a
    // full-list replace — offering it here would be a delete button with no target.
    it('offers no delete affordance on a new group', () => {
      render(<GroupEditorForm groupId={null} sensors={[]} />);
      expect(screen.queryByText('Delete group')).toBeNull();
    });

    // Intent (data-loss guard): deleteGroup POSTs the loaded list minus this id. Before
    // loadGroups() resolves that list is empty, so a click would replace the server's
    // groups with [] — wiping every other group. Gate the button on the group actually
    // being loaded, not merely on the route carrying an id.
    it('offers no delete affordance while the group is not in the loaded list', () => {
      groups.value = [];
      render(<GroupEditorForm groupId="living_room" sensors={[]} />);
      expect(screen.queryByText('Delete group')).toBeNull();
    });

    // Intent: a destructive, irreversible action must never fire on a single click.
    it('arms on the first click without deleting', () => {
      const spy = vi.spyOn(groupsState, 'deleteGroup').mockResolvedValue(undefined);
      render(<GroupEditorForm groupId="living_room" sensors={[]} />);
      fireEvent.click(screen.getByText('Delete group'));
      expect(screen.getByText('Confirm delete')).toBeTruthy();
      expect(spy).not.toHaveBeenCalled();
    });

    // Intent: confirming must delete THIS group and land the operator back on the list
    // screen — leaving them in an editor for a group that no longer exists is a dead end.
    it('deletes the group and returns to #/detectors once confirmed', async () => {
      const spy = vi.spyOn(groupsState, 'deleteGroup').mockImplementation(async () => {
        saveState.value = { result: { ok: true, count: 0 } };
      });
      location.hash = '#/groups/living_room';
      render(<GroupEditorForm groupId="living_room" sensors={[]} />);
      fireEvent.click(screen.getByText('Delete group'));
      fireEvent.click(screen.getByText('Confirm delete'));
      await vi.waitFor(() => expect(spy).toHaveBeenCalledWith('living_room'));
      await vi.waitFor(() => expect(location.hash).toBe('#/detectors'));
    });

    // Intent: a failed delete must NOT read as success. Navigating away would hide
    // GroupSaveResultBanner's reason and let the operator believe the group is gone.
    it('stays in the editor when the delete fails', async () => {
      const spy = vi.spyOn(groupsState, 'deleteGroup').mockImplementation(async () => {
        saveState.value = { result: { ok: false, kind: 'error', reason: 'disk error' } };
      });
      location.hash = '#/groups/living_room';
      render(<GroupEditorForm groupId="living_room" sensors={[]} />);
      fireEvent.click(screen.getByText('Delete group'));
      fireEvent.click(screen.getByText('Confirm delete'));
      await vi.waitFor(() => expect(spy).toHaveBeenCalledWith('living_room'));
      expect(location.hash).toBe('#/groups/living_room');
    });
  });
});
