import type { ComponentChildren } from 'preact';
import { Sidebar } from './Sidebar';

interface AppShellProps {
  children: ComponentChildren;
}

// Sidebar-based shell (D-01) — replaces the old top-header/footer layout.
// Width comes from --content-max in argus.css, not an inline style.
export function AppShell({ children }: AppShellProps) {
  return (
    <div class="argus-shell">
      <Sidebar />
      <main class="argus-main">{children}</main>
    </div>
  );
}
