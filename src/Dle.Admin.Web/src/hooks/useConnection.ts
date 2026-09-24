import { useSyncExternalStore } from 'react';
import { getConnection, subscribeConnection, type Connection } from '../api/client';

export function useConnection(): Connection | null {
  return useSyncExternalStore(subscribeConnection, getConnection, getConnection);
}
