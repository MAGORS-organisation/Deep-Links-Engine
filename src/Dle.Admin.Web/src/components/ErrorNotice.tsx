import { Link } from 'react-router';
import { ApiError } from '../api/client';
import { describeError } from '../domain/format';
import { useT } from '../i18n';

export interface ErrorNoticeProps {
  error: unknown;
  onRetry?: () => void;
  /** Hide the per-field list (the form shows them inline instead). */
  compact?: boolean;
}

/** One place that turns a failed call into words, field list included. */
export function ErrorNotice({ error, onRetry, compact }: ErrorNoticeProps) {
  const t = useT();
  if (error === undefined || error === null) {
    return null;
  }

  const api = error instanceof ApiError ? error : null;
  const fields = api ? Object.entries(api.errors) : [];
  const needsSettings = api?.key === 'no_credential';

  return (
    <div role="alert" className="callout callout-bad">
      <strong>{describeError(t, error)}</strong>
      {api?.detail && api.key !== 'ValidationFailed' && api.key !== 'UnsafeTarget' && <span>{api.detail}</span>}
      {!compact && fields.length > 0 && (
        <ul className="small" style={{ margin: 0, paddingLeft: '1.2em' }}>
          {fields.map(([path, messages]) => (
            <li key={path}>
              <code>{path}</code>: {messages.join(' ')}
            </li>
          ))}
        </ul>
      )}
      <div className="row">
        {onRetry && !needsSettings && (
          <button type="button" className="btn btn-sm" onClick={onRetry}>
            {t('action.retry')}
          </button>
        )}
        {(needsSettings || api?.isUnauthorized) && (
          <Link className="btn btn-sm" to="/settings">
            {t('state.goToSettings')}
          </Link>
        )}
      </div>
    </div>
  );
}
