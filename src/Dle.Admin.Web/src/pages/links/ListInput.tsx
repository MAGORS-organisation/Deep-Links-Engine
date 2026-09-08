import { useEffect, useState } from 'react';
import { parseIntList, parseList } from '../../domain/format';

interface BaseProps {
  id?: string;
  'aria-describedby'?: string | undefined;
  'aria-invalid'?: true | undefined;
  placeholder?: string;
  mono?: boolean;
  transform?: (value: string) => string;
}

/**
 * A comma separated list bound to a string array. Keeps its own text while the operator types so
 * a trailing comma is not normalized away mid-keystroke; re-syncs only when the array changes
 * from outside (duplicate, reorder, load).
 */
export function ListInput({ value, onChange, transform, mono, ...rest }: BaseProps & { value: string[]; onChange: (next: string[]) => void }) {
  const joined = value.join(', ');
  const [text, setText] = useState(joined);
  const fromText = parseList(text).join(', ');

  useEffect(() => {
    if (joined !== fromText) {
      setText(joined);
    }
  }, [joined, fromText]);

  return (
    <input
      {...rest}
      className={mono ? 'input mono' : 'input'}
      value={text}
      onChange={(e) => {
        const next = transform ? transform(e.target.value) : e.target.value;
        setText(next);
        onChange(parseList(next));
      }}
      autoComplete="off"
      spellCheck={false}
    />
  );
}

export function IntListInput({ value, onChange, mono, ...rest }: BaseProps & { value: number[]; onChange: (next: number[]) => void }) {
  const joined = value.join(', ');
  const [text, setText] = useState(joined);
  const fromText = parseIntList(text).join(', ');

  useEffect(() => {
    if (joined !== fromText) {
      setText(joined);
    }
  }, [joined, fromText]);

  return (
    <input
      {...rest}
      className={mono ? 'input mono' : 'input'}
      value={text}
      inputMode="numeric"
      onChange={(e) => {
        setText(e.target.value);
        onChange(parseIntList(e.target.value));
      }}
      autoComplete="off"
    />
  );
}
