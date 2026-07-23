import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/preact';
import { GroupEditorForm } from './GroupEditorForm';
import * as client from '../api/client';
import { groups, draftFriendlyName, draftGroupId, draftMembers } from '../state/groups';
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
});
