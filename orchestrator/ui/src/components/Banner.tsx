import type { ComponentChildren } from 'preact';

export interface BannerProps {
  tone?: 'success' | 'error' | 'validation' | 'reloading' | 'info';
  children: ComponentChildren;
  action?: ComponentChildren;
  onDismiss?: () => void;
}

// Consolidated tone-driven banner — Wave 3 replaces SaveResultBanner, GroupSaveResultBanner,
// and AreaSuggestionBanner call sites with this shared component. Defaults to role="status"
// (existing banners branch alert vs status per result kind; callers needing role="alert" pass
// it through their own wrapper markup until a richer semantics prop is needed).
export function Banner({ tone = 'info', children, action, onDismiss }: BannerProps) {
  return (
    <div class={`argus-banner argus-banner--${tone}`} role="status">
      <span>{children}</span>
      {action}
      {onDismiss && (
        <button
          type="button"
          class="argus-banner__dismiss"
          aria-label="Dismiss"
          onClick={onDismiss}
        >
          ✕
        </button>
      )}
    </div>
  );
}
