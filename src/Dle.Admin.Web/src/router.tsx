import { lazy } from 'react';
import { createHashRouter } from 'react-router';
import { Layout } from './app/Layout';

/*
 * Hash routing on purpose. The control plane serves wwwroot/admin/ as static files and keeps its
 * own SPA fallback pointed at the committed no-Node console (Dle:Control:SpaIndexFile), so a
 * history-mode deep link such as /admin/links would be answered by that page rather than by this
 * bundle. With the hash router every route lives under /admin/#/…, which is one static file.
 */
const DashboardPage = lazy(() => import('./pages/DashboardPage'));
const LinksPage = lazy(() => import('./pages/LinksPage'));
const LinkEditorPage = lazy(() => import('./pages/LinkEditorPage'));
const SimulatorPage = lazy(() => import('./pages/SimulatorPage'));
const DomainsPage = lazy(() => import('./pages/DomainsPage'));
const AppsPage = lazy(() => import('./pages/AppsPage'));
const WebhooksPage = lazy(() => import('./pages/WebhooksPage'));
const AbusePage = lazy(() => import('./pages/AbusePage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));

export function createAppRouter() {
  return createHashRouter([
    {
      path: '/',
      element: <Layout />,
      children: [
        { index: true, element: <DashboardPage /> },
        { path: 'links', element: <LinksPage /> },
        { path: 'links/new', element: <LinkEditorPage /> },
        { path: 'links/:id', element: <LinkEditorPage /> },
        { path: 'simulator', element: <SimulatorPage /> },
        { path: 'simulator/:id', element: <SimulatorPage /> },
        { path: 'domains', element: <DomainsPage /> },
        { path: 'apps', element: <AppsPage /> },
        { path: 'webhooks', element: <WebhooksPage /> },
        { path: 'abuse', element: <AbusePage /> },
        { path: 'settings', element: <SettingsPage /> },
        { path: '*', element: <DashboardPage /> },
      ],
    },
  ]);
}
