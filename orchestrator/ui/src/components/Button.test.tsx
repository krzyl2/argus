import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/preact';
import { Button } from './Button';

describe('Button', () => {
  it('renders variant primary + size md with the composed BEM class list', () => {
    const { container } = render(
      <Button variant="primary" size="md">
        Save
      </Button>
    );
    const button = container.querySelector('button') as HTMLButtonElement;
    expect(button.className).toContain('argus-btn');
    expect(button.className).toContain('argus-btn--primary');
    expect(button.className).toContain('argus-btn--md');
  });

  it('renders variant destructive-ghost class', () => {
    const { container } = render(<Button variant="destructive-ghost">Delete group</Button>);
    const button = container.querySelector('button') as HTMLButtonElement;
    expect(button.className).toContain('argus-btn--destructive-ghost');
  });

  it('calls onClick once when clicked', () => {
    const onClick = vi.fn();
    const { container } = render(<Button onClick={onClick}>Click me</Button>);
    const button = container.querySelector('button') as HTMLButtonElement;
    fireEvent.click(button);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('renders the spinner and disables the button when loading', () => {
    const { container } = render(<Button loading>Save</Button>);
    const button = container.querySelector('button') as HTMLButtonElement;
    const spinner = container.querySelector('.argus-btn__spinner');
    expect(spinner).not.toBeNull();
    expect(button.disabled).toBe(true);
  });

  it('renders the exact children label given (parent-owned label swap)', () => {
    const { container } = render(<Button variant="destructive-ghost">Confirm delete</Button>);
    const button = container.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toBe('Confirm delete');
  });
});
