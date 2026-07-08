import { describe, it, expect, afterEach } from 'vitest';
import { render } from '@testing-library/preact';
import { fireEvent } from '@testing-library/preact';
import { Sidebar } from './Sidebar';

describe('Sidebar (D-02 nav items + THEME-02 toggle)', () => {
  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    localStorage.removeItem('argus-theme');
  });

  it('renders 5 nav items with 3 disabled', () => {
    const { container } = render(<Sidebar />);
    const items = container.querySelectorAll('.argus-sidebar__item');
    expect(items.length).toBe(5);

    const disabled = container.querySelectorAll('.argus-sidebar__item--disabled');
    expect(disabled.length).toBe(3);
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
