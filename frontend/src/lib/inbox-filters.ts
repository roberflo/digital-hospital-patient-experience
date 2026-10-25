export type InboxFilters = {
  state: string;
  assignment: string;
  channelId: string;
  priority: string;
  label: string;
  /** Who attends: all, human or agent. Travels as `status=` (MR-18). */
  mode: string;
};
export const defaultInboxFilters: InboxFilters = {
  state: 'open',
  assignment: 'all',
  channelId: '',
  priority: '',
  label: '',
  mode: 'all',
};
export function inboxQuery(f: InboxFilters, page: number) {
  const q = new URLSearchParams({ assignment: f.assignment, page: String(page) });
  if (f.state) q.set('state', f.state);
  if (f.mode !== 'all') q.set('status', f.mode);
  if (f.channelId) q.set('channelId', f.channelId);
  if (f.priority) q.set('priority', f.priority);
  if (f.label.trim()) q.set('label', f.label.trim());
  return q;
}
/** An empty list under these filters is «no results», not «all caught up». */
export const filtersActive = (f: InboxFilters, search: string) =>
  !!search.trim() ||
  (Object.keys(defaultInboxFilters) as (keyof InboxFilters)[]).some(
    (k) => f[k].trim() !== defaultInboxFilters[k],
  );
/** A stored view, complete: the API's defaults (`ViewFilters`) fill what it lacks. */
export const fromView = (saved: Partial<InboxFilters>): InboxFilters => ({
  ...defaultInboxFilters,
  state: '',
  ...saved,
});
/** A saved view is the current one while every filter matches it (MR-57). */
export const sameFilters = (a: InboxFilters, b: InboxFilters) =>
  (Object.keys(defaultInboxFilters) as (keyof InboxFilters)[]).every((k) => a[k] === b[k]);
