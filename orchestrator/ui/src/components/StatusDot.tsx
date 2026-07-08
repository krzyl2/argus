export interface StatusDotProps {
  status?: 'ok' | 'warn' | 'error' | 'idle';
  label?: string;
}

// Tri-state status dot over .argus-status-dot / .status-{status}. The dot itself is always
// decorative (aria-hidden) — when a label is supplied it is wrapped together with the dot in
// .argus-status (existing flex class) so screen readers get the text while the dot conveys no
// information on its own. Never renders an emoji or icon for state.
export function StatusDot({ status = 'ok', label }: StatusDotProps) {
  const dot = <span class={`argus-status-dot status-${status}`} aria-hidden="true" />;
  if (!label) return dot;
  return (
    <span class="argus-status">
      {dot}
      {label}
    </span>
  );
}
