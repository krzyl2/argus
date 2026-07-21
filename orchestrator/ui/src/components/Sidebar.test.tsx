import { describe, it, expect, afterEach } from 'vitest';
import { render } from '@testing-library/preact';
import { fireEvent } from '@testing-library/preact';
import { Sidebar } from './Sidebar';
import { route } from '../router';

describe('Sidebar (D-02 nav items + THEME-02 toggle)', () => {
  const originalRoute = route.value;

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    localStorage.removeItem('argus-theme');
    route.value = originalRoute;
  });

  it('renders 5 nav items, all enabled (Phase 11 D-10)', () => {
    const { container } = render(<Sidebar />);
    const items = container.querySelectorAll('.argus-sidebar__item');
    expect(items.length).toBe(5);

    const disabled = container.querySelectorAll('.argus-sidebar__item--disabled');
    expect(disabled.length).toBe(0);
  });

  it('renders Detectors + Add detector, and no Sensors/Groups items (D-02/D-04)', () => {
    const { container } = render(<Sidebar />);
    const labels = Array.from(container.querySelectorAll('.argus-sidebar__label')).map(
      (el) => el.textContent
    );

    expect(labels).toContain('Detectors');
    expect(labels).toContain('Add detector');
    expect(labels).not.toContain('Sensors');
    expect(labels).not.toContain('Groups');
  });

  it('highlights Detectors for /detectors/* sub-routes (D-04)', () => {
    route.value = '/detectors/sensor/sensor.living_room_temp';
    const { container } = render(<Sidebar />);
    const items = Array.from(container.querySelectorAll('.argus-sidebar__item'));
    const detectorsItem = items.find((el) =>
      el.querySelector('.argus-sidebar__label')?.textContent === 'Detectors'
    );

    expect(detectorsItem?.classList.contains('argus-sidebar__item--active')).toBe(true);
  });

  it('clicking the theme toggle sets data-theme and localStorage["argus-theme"]', () => {
    document.documentElement.removeAttribute('data-theme');
    const { container } = render(<Sidebar />);
    const toggle = container.querySelector('.argus-sidebar__theme-toggle') as HTMLButtonElement;
    expect(toggle).toBeTruthy();

    fireEvent.click(toggle);

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('argus-theme')).toBe('dark');

    fireEvent.click(toggle);

    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem('argus-theme')).toBe('light');
  });
});
