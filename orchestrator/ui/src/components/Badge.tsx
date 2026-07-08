import type { ComponentChildren } from 'preact';

export interface BadgeProps {
  tone?: 'tracked' | 'member' | 'neutral' | 'ok' | 'warn' | 'error' | 'accent';
  children: ComponentChildren;
}

// Tone-driven pill/badge over .argus-pill / .argus-pill--{tone}. All tone classes already
// exist in argus.css (Plan 10-01) — this component only composes them.
export function Badge({ tone = 'neutral', children }: BadgeProps) {
  return <span class={`argus-pill argus-pill--${tone}`}>{children}</span>;
}
