import { Suspense } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router';
import { tenants } from '../api';
import { EmptyState } from '../components/EmptyState';
import { ErrorBoundary } from '../components/ErrorBoundary';
import { useAsync } from '../hooks/useAsync';
import { useConnection } from '../hooks/useConnection';
import { useTheme, type Theme } from '../hooks/useTheme';
import { LOCALES, useI18n, type Locale } from '../i18n';
import styles from './Layout.module.css';

const NAV: { to: string; key: 'nav.dashboard' | 'nav.links' | 'nav.simulator' | 'nav.domains' | 'nav.apps' | 'nav.webhooks' | 'nav.abuse' | 'nav.settings'; end?: boolean }[] = [
  { to: '/', key: 'nav.dashboard', end: true },
  { to: '/links', key: 'nav.links' },
  { to: '/simulator', key: 'nav.simulator' },
  { to: '/domains', key: 'nav.domains' },
  { to: '/apps', key: 'nav.apps' },
  { to: '/webhooks', key: 'nav.webhooks' },
  { to: '/abuse', key: 'nav.abuse' },
  { to: '/settings', key: 'nav.settings' },
];

export function Layout() {
  const { t, locale, setLocale } = useI18n();
  const [theme, setTheme] = useTheme();
  const connection = useConnection();
  const location = useLocation();
  const onSettings = location.pathname.startsWith('/settings');

  const me = useAsync((signal) => tenants.me(signal), [connection?.apiKey, connection?.baseUrl], connection !== null);

  return (
    <div className={styles.shell}>
      <a href="#main" className={styles.skip}>
        {t('app.skipToContent')}
      </a>
      <aside className={styles.sidebar}>
        <Link to="/" className={styles.brand}>
          <span className={styles.brandMark} aria-hidden="true">
            DLE
          </span>
          <span className={styles.brandText}>
            <span>{t('app.name')}</span>
            <span className={styles.brandSub}>{t('app.console')}</span>
          </span>
        </Link>
        <nav aria-label={t('nav.main')} className={styles.nav}>
          {NAV.map((item) => (
            <NavLink key={item.to} to={item.to} end={item.end} className={({ isActive }) => [styles.navItem, isActive ? styles.navActive : ''].filter(Boolean).join(' ')}>
              {t(item.key)}
            </NavLink>
          ))}
        </nav>
        <div className={styles.sidebarFooter}>
          <div className={styles.tenant}>
            <span className={styles.tenantLabel}>{t('app.tenant')}</span>
            <span className={styles.tenantName}>{connection ? (me.data?.name ?? (me.loading ? t('app.loading') : '—')) : t('state.notConnected')}</span>
          </div>
          <div className={styles.prefs}>
            <label className={styles.pref}>
              <span className="sr-only">{t('label.language')}</span>
              <select className="select" value={locale} onChange={(e) => setLocale(e.target.value as Locale)} aria-label={t('label.language')}>
                {LOCALES.map((l) => (
                  <option key={l} value={l}>
                    {l.toUpperCase()}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.pref}>
              <span className="sr-only">{t('label.theme')}</span>
              <select className="select" value={theme} onChange={(e) => setTheme(e.target.value as Theme)} aria-label={t('label.theme')}>
                <option value="system">{t('theme.system')}</option>
                <option value="light">{t('theme.light')}</option>
                <option value="dark">{t('theme.dark')}</option>
              </select>
            </label>
          </div>
        </div>
      </aside>
      <main id="main" className={styles.main} tabIndex={-1}>
        <ErrorBoundary fallbackTitle={t('state.unexpected')} retryLabel={t('action.retry')}>
          {connection || onSettings ? (
            <Suspense fallback={<p className="muted">{t('app.loading')}</p>}>
              <Outlet />
            </Suspense>
          ) : (
            <EmptyState
              title={t('state.notConnected')}
              detail={t('state.notConnectedDetail')}
              action={
                <Link className="btn btn-primary" to="/settings">
                  {t('state.goToSettings')}
                </Link>
              }
            />
          )}
        </ErrorBoundary>
      </main>
    </div>
  );
}
