import { useId, type KeyboardEvent, type ReactNode } from 'react';
import styles from './Tabs.module.css';

export interface Tab {
  id: string;
  label: ReactNode;
  disabled?: boolean;
}

export interface TabsProps {
  tabs: Tab[];
  active: string;
  onChange: (id: string) => void;
  label: string;
  /**
   * Base id for the tab and panel element ids. Pass one (from `useId()`) when you render the
   * panels yourself with `tabPanelProps`, so that `aria-controls` on each tab points at its panel.
   * Generated when omitted.
   */
  id?: string;
}

/**
 * WAI-ARIA tabs with roving focus (arrow keys, Home, End). Panels are rendered by the caller;
 * see `tabPanelProps` for the matching panel attributes.
 *
 * The key handler lives on each tab button rather than on the tablist container: the container is
 * not itself focusable in the roving-tabindex pattern, and a key handler on a non-focusable element
 * is exactly what jsx-a11y's interactive-supports-focus rule flags.
 */
export function Tabs({ tabs, active, onChange, label, id: idProp }: TabsProps) {
  const generatedId = useId();
  const id = idProp ?? generatedId;

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    const enabled = tabs.filter((t) => !t.disabled);
    const index = enabled.findIndex((t) => t.id === active);
    if (index < 0) {
      return;
    }
    let next = index;
    if (event.key === 'ArrowRight') next = (index + 1) % enabled.length;
    else if (event.key === 'ArrowLeft') next = (index - 1 + enabled.length) % enabled.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = enabled.length - 1;
    else return;
    event.preventDefault();
    const target = enabled[next];
    if (target) {
      onChange(target.id);
      document.getElementById(`${id}-${target.id}`)?.focus();
    }
  };

  return (
    <div role="tablist" aria-label={label} className={styles.list}>
      {tabs.map((tab) => (
        <button
          key={tab.id}
          id={`${id}-${tab.id}`}
          type="button"
          role="tab"
          aria-selected={tab.id === active}
          aria-controls={`${id}-panel-${tab.id}`}
          tabIndex={tab.id === active ? 0 : -1}
          disabled={tab.disabled}
          className={[styles.tab, tab.id === active ? styles.active : ''].filter(Boolean).join(' ')}
          onClick={() => onChange(tab.id)}
          onKeyDown={onKeyDown}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
