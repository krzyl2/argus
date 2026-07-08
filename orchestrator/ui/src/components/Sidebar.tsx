import { useState } from 'preact/hooks';
import { route } from '../router';

type Theme = 'light' | 'dark';

interface NavItem {
  id: string;
  label: string;
  icon: string;
  href?: string;
  disabled?: boolean;
}

// D-02: final order/look for all 5 screens now; Dashboard/Algorithms/Settings
// are disabled placeholders until Phase 11 adds their routes.
const NAV_ITEMS: NavItem[] = [
  { id: 'dashboard', label: 'Dashboard', icon: '▦', disabled: true },
  { id: 'algorithms', label: 'Algorithms', icon: '⚙', disabled: true },
  { id: 'sensors', label: 'Sensors', icon: '◎', href: '#/sensors' },
  { id: 'groups', label: 'Groups', icon: '⧉', href: '#/groups' },
  { id: 'settings', label: 'Settings', icon: '⚙', disabled: true },
];

function isActive(item: NavItem, currentRoute: string): boolean {
  if (item.id === 'sensors') return currentRoute === '/sensors';
  if (item.id === 'groups') return currentRoute === '/groups' || currentRoute.startsWith('/groups/');
  return false;
}

function handleNavigate(item: NavItem) {
  if (item.disabled || !item.href) return;
  location.hash = item.href;
}

export function Sidebar() {
  const currentRoute = route.value;
  const [theme, setTheme] = useState<Theme>(
    (document.documentElement.getAttribute('data-theme') as Theme | null) ?? 'light'
  );

  function handleThemeToggle() {
    const next: Theme = theme === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    localStorage.setItem('argus-theme', next);
    setTheme(next);
  }

  return (
    <nav class="argus-sidebar">
      <div class="argus-sidebar__brand">
        <span aria-hidden="true" class="argus-sidebar__brand-mark">
          A
        </span>
        <span class="argus-sidebar__wordmark">Argus</span>
      </div>

      {NAV_ITEMS.map((item) => {
        const active = isActive(item, currentRoute);
        const classes = [
          'argus-sidebar__item',
          active ? 'argus-sidebar__item--active' : '',
          item.disabled ? 'argus-sidebar__item--disabled' : '',
        ]
          .filter(Boolean)
          .join(' ');
        return (
          <button
            key={item.id}
            type="button"
            class={classes}
            disabled={item.disabled}
            onClick={() => handleNavigate(item)}
          >
            <span aria-hidden="true" class="argus-sidebar__icon">
              {item.icon}
            </span>
            <span class="argus-sidebar__label">{item.label}</span>
          </button>
        );
      })}

      <div class="argus-sidebar__footer">
        <button
          type="button"
          class="argus-sidebar__theme-toggle"
          onClick={handleThemeToggle}
          aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        >
          <span aria-hidden="true">{theme === 'dark' ? '☀' : '☾'}</span>
        </button>
      </div>
    </nav>
  );
}
