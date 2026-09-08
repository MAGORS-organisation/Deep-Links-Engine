import { useId, type ReactNode } from 'react';

export interface FieldProps {
  label: ReactNode;
  hint?: ReactNode;
  error?: string | undefined;
  /** Adds "(optional)" after the label. */
  optionalText?: string;
  className?: string;
  children: (props: { id: string; 'aria-describedby': string | undefined; 'aria-invalid': true | undefined }) => ReactNode;
}

/** Label, hint and error wiring for one control. The child receives the ids to attach. */
export function Field({ label, hint, error, optionalText, className, children }: FieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;

  return (
    <div className={['field', className ?? ''].filter(Boolean).join(' ')}>
      <label htmlFor={id}>
        {label}
        {optionalText && <span className="faint"> ({optionalText})</span>}
      </label>
      {children({ id, 'aria-describedby': describedBy, 'aria-invalid': error ? true : undefined })}
      {hint && (
        <span id={hintId} className="hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={errorId} className="error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
