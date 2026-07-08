import { render } from 'preact';
import './router';
import { route } from './router';
import { AppShell } from './components/AppShell';
import { SensorsPage } from './components/SensorsPage';
import { GroupsPage } from './components/GroupsPage';
import { DashboardPage } from './components/DashboardPage';
import { AlgorithmsPage } from './components/AlgorithmsPage';
import { SettingsPage } from './components/SettingsPage';

function App() {
  // route.value is reactive.
  const isGroupsRoute = route.value === '/groups' || route.value.startsWith('/groups/');
  let page;
  if (route.value === '/dashboard') {
    page = <DashboardPage />;
  } else if (route.value === '/algorithms') {
    page = <AlgorithmsPage />;
  } else if (route.value === '/settings') {
    page = <SettingsPage />;
  } else if (isGroupsRoute) {
    page = <GroupsPage />;
  } else {
    page = <SensorsPage />;
  }
  return <AppShell>{page}</AppShell>;
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
