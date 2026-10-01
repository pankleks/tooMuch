/** Serializes read-modify-write operations for one device, not whole requests. */
export function createWriteLock() {
  const pending = new Map<string, Promise<void>>();
  return async function withWriteLock<T>(key: string, operation: () => Promise<T>): Promise<T> {
    const previous = pending.get(key) ?? Promise.resolve();
    let release!: () => void;
    const current = new Promise<void>(resolve => { release = resolve; });
    pending.set(key, current);
    await previous;
    try {
      return await operation();
    } finally {
      release();
      if (pending.get(key) === current) pending.delete(key);
    }
  };
}
