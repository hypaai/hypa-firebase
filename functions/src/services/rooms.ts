import { ROOMS_CREATED_FIELD, ROOMS_MAX_AGE_MS, ROOMS_PATH } from "../config";

/** The slice of the Admin Realtime Database the cleanup uses (so tests can fake it). */
export interface RoomsDb {
  ref(path: string): {
    orderByChild(field: string): {
      endAt(value: number): {
        limitToFirst(n: number): {
          get(): Promise<{
            exists(): boolean;
            forEach(cb: (child: { key: string | null }) => boolean | void): boolean;
          }>;
        };
      };
    };
    update(values: Record<string, null>): Promise<void>;
  };
}

const BATCH = 200;

/**
 * Deletes rooms whose `createdAt` (epoch ms) is older than `maxAgeMs` (default 2 h); returns how many.
 * Rooms without a numeric createdAt never match, so a malformed room is left alone.
 * ASSUMPTION (G09 owns the model): rooms live at /rooms/{roomId} with a numeric createdAt.
 */
export async function deleteStaleRooms(db: RoomsDb, nowMs: number, maxAgeMs = ROOMS_MAX_AGE_MS): Promise<number> {
  const rooms = db.ref(ROOMS_PATH);
  let removed = 0;
  for (;;) {
    const snap = await rooms
      .orderByChild(ROOMS_CREATED_FIELD)
      .endAt(nowMs - maxAgeMs)
      .limitToFirst(BATCH)
      .get();
    const gone: Record<string, null> = {};
    snap.forEach((child) => {
      if (child.key) gone[child.key] = null;
    });
    const count = Object.keys(gone).length;
    if (count === 0) return removed;
    await rooms.update(gone);
    removed += count;
    if (count < BATCH) return removed;
  }
}
