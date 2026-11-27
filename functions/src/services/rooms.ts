import type { Database } from "firebase-admin/database";
import { ROOMS_CREATED_FIELD, ROOMS_MAX_AGE_MS, ROOMS_PATH } from "../config";

const BATCH = 200;

/** Delete rooms with a numeric Unix timestamp strictly older than the cutoff. */
export async function deleteStaleRooms(
  db: Pick<Database, "ref">,
  nowMs: number,
  maxAgeMs = ROOMS_MAX_AGE_MS,
): Promise<number> {
  const cutoff = nowMs - maxAgeMs;
  if (cutoff <= 0) return 0;

  const rooms = db.ref(ROOMS_PATH);
  // Realtime Database sorts null and booleans before numbers. A numeric lower bound excludes them.
  const stale = rooms.orderByChild(ROOMS_CREATED_FIELD).startAt(0).endBefore(cutoff).limitToFirst(BATCH);
  let removed = 0;
  for (;;) {
    const snap = await stale.get();
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
