import { useCallback, useEffect, useMemo, useState } from 'react';
import { RouterProvider } from 'react-router';
import { ToastProvider } from './components/Toast';
import { I18nContext, readStoredLocale, storeLocale, useI18nValue, type Locale } from './i18n';
import { createAppRouter } from './router';

export function App() {
  const [locale, setLocaleState] = useState<Locale>(readStoredLocale);

  const setLocale = useCallback((next: Locale) => {
    setLocaleState(next);
    storeLocale(next);
  }, []);

  useEffect(() => {
    document.documentElement.lang = locale;
  }, [locale]);

  const i18n = useI18nValue(locale, setLocale);
  const router = useMemo(() => createAppRouter(), []);

  return (
    <I18nContext.Provider value={i18n}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </I18nContext.Provider>
  );
}
