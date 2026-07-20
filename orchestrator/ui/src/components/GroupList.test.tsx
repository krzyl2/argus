import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { GroupList } from './GroupList';
import type { GroupConfig } from '../api/types';

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

describe('GroupList', () => {
  it('wraps the group rows in a Card', () => {
    const groups = [makeGroup()];
    const { container } = render(<GroupList groups={groups} />);
    expect(container.querySelector('.argus-card ul.argus-list')).not.toBeNull();
  });

  it('renders one row per group', () => {
    const groups = [
      makeGroup({ groupId: 'living_room', friendlyName: 'Living Room' }),
      makeGroup({ groupId: 'bedroom', friendlyName: 'Bedroom' }),
    ];
    const { container } = render(<GroupList groups={groups} />);
    expect(container.querySelectorAll('.argus-list-row').length).toBe(2);
  });

  it('renders the custom empty-state branch for zero groups (not the sensor EmptyState)', () => {
    const { container } = render(<GroupList groups={[]} />);
    expect(container.querySelector('.argus-empty')).not.toBeNull();
    expect(container.querySelector('.argus-card')).toBeNull();
    expect(container.textContent).toMatch(/No groups configured/);
  });
});
