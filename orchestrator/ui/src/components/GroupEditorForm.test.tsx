import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/preact';
import { GroupEditorForm } from './GroupEditorForm';
import * as client from '../api/client';
import { groups, draftFriendlyName, draftGroupId } from '../state/groups';
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
});
