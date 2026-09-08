import { createContext, useCallback, useContext, useMemo } from 'react';
import { en, type MessageKey } from './en';
import { sk } from './sk';

export type Locale = 'en' | 'sk';

export const LOCALES: readonly Locale[] = ['en', 'sk'];

const catalogues: Record<Locale, Record<MessageKey, string>> = { en, sk };

export type Params = Record<string, string | number>;

export type Translate = (key: MessageKey, params?: Params) => string;

export interface I18n {
  locale: Locale;
  t: Translate;
  setLocale: (locale: Locale) => void;
  /** Intl helpers bound to the locale. */
  formatDate: (value: string | Date | null | undefined) => string;
  formatNumber: (value: number | null | undefined, options?: Intl.NumberFormatOptions) => string;
  formatPercent: (ratio: number) => string;
}

export const I18nContext = createContext<I18n | null>(null);

const LOCALE_KEY = 'dle.admin.locale';

export function readStoredLocale(): Locale {
  try {
    const stored = localStorage.getItem(LOCALE_KEY);
    if (stored === 'en' || stored === 'sk') {
      return stored;
    }
  } catch {
    // ignore
  }
  const nav = typeof navigator !== 'undefined' ? navigator.language.toLowerCase() : 'en';
  return nav.startsWith('sk') ? 'sk' : 'en';
}

export function storeLocale(locale: Locale): void {
  try {
    localStorage.setItem(LOCALE_KEY, locale);
  } catch {
    // ignore
  }
}

export function interpolate(template: string, params?: Params): string {
  if (!params) {
    return template;
  }
  return template.replace(/\{(\w+)\}/g, (match, name: string) => {
    const value = params[name];
    return value === undefined ? match : String(value);
  });
}

export function translate(locale: Locale, key: MessageKey, params?: Params): string {
  const catalogue = catalogues[locale];
  const template = catalogue[key] ?? en[key] ?? key;
  return interpolate(template, params);
}

/** Builds the context value. Exported so main.tsx can create it once per locale change. */
export function useI18nValue(locale: Locale, setLocale: (locale: Locale) => void): I18n {
  const t = useCallback<Translate>((key, params) => translate(locale, key, params), [locale]);

  return useMemo<I18n>(() => {
    const dateFormat = new Intl.DateTimeFormat(locale === 'sk' ? 'sk-SK' : 'en-GB', {
      dateStyle: 'medium',
      timeStyle: 'short',
    });
    const numberFormat = new Intl.NumberFormat(locale === 'sk' ? 'sk-SK' : 'en-GB');
    const percentFormat = new Intl.NumberFormat(locale === 'sk' ? 'sk-SK' : 'en-GB', {
      style: 'percent',
      maximumFractionDigits: 1,
    });

    return {
      locale,
      t,
      setLocale,
      formatDate: (value) => {
        if (!value) {
          return '—';
        }
        const date = typeof value === 'string' ? new Date(value) : value;
        return Number.isNaN(date.getTime()) ? String(value) : dateFormat.format(date);
      },
      formatNumber: (value, options) =>
        value === null || value === undefined
          ? '—'
          : options
            ? new Intl.NumberFormat(locale === 'sk' ? 'sk-SK' : 'en-GB', options).format(value)
            : numberFormat.format(value),
      formatPercent: (ratio) => percentFormat.format(ratio),
    };
  }, [locale, t, setLocale]);
}

export function useI18n(): I18n {
  const value = useContext(I18nContext);
  if (!value) {
    throw new Error('useI18n must be used inside I18nContext.Provider');
  }
  return value;
}

/** The hook pages use: `const t = useT(); t('links.title')`. */
export function useT(): Translate {
  return useI18n().t;
}

export type { MessageKey };
