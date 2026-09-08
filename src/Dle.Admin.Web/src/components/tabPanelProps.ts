/**
 * Props for the panel that belongs to a tab rendered by {@link Tabs}. Kept in its own module so
 * `Tabs.tsx` exports only components, which is what keeps React Fast Refresh working for it.
 */
export function tabPanelProps(tablistId: string, tabId: string) {
  return { role: 'tabpanel' as const, id: `${tablistId}-panel-${tabId}`, tabIndex: 0 };
}
