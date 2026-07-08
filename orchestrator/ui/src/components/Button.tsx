import type { ComponentChildren } from 'preact';

export interface ButtonProps {
  variant?: 'primary' | 'secondary' | 'ghost' | 'destructive-ghost';
  size?: 'md' | 'sm' | 'xs';
  disabled?: boolean;
  loading?: boolean;
  type?: 'button' | 'submit';
  onClick?: () => void;
  ariaLabel?: string;
  children: ComponentChildren;
}

// Shared button wrapper over .argus-btn / .argus-btn--{variant} / .argus-btn--{size}.
// Does not own any arm/confirm timer state — callers (e.g. GroupListRow) pass the
// current label as children and manage their own armed state.
export function Button({
  variant = 'primary',
  size = 'md',
  disabled = false,
  loading = false,
  type = 'button',
  onClick,
  ariaLabel,
  children,
}: ButtonProps) {
  return (
    <button
      type={type}
      class={`argus-btn argus-btn--${variant} argus-btn--${size}`}
      disabled={disabled || loading}
      aria-label={ariaLabel}
      onClick={onClick}
    >
      {loading && <span class="argus-btn__spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}
