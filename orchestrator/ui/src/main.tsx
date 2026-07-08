import { render } from 'preact';
import './router';
import { route } from './router';
import { AppShell } from './components/AppShell';
import { SensorsPage } from './components/SensorsPage';
import { GroupsPage } from './components/GroupsPage';

function App() {
  // route.value is reactive.
  const isGroupsRoute = route.value === '/groups' || route.value.startsWith('/groups/');
  return <AppShell>{isGroupsRoute ? <GroupsPage /> : <SensorsPage />}</AppShell>;
}

const storedTheme = localStorage.getItem('argus-theme');
if (storedTheme) {
  document.documentElement.setAttribute('data-theme', storedTheme);
} else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
  document.documentElement.setAttribute('data-theme', 'dark');
}

const mountEl = document.getElementById('app');
if (mountEl) {
  render(<App />, mountEl);
}
