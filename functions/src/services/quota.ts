import { Timestamp, type Firestore } from "firebase-admin/firestore";
import { USAGE_RETENTION_DAYS } from "../config";
import { ProxyError, type ErrorExtras } from "../api/errors";
import { dayKey, nextReset } from "../utils/time";
import type { Limits } from "./limits";

export interface Usage {
  seconds: number;
  requests: number;
}
export type Cost = Usage;
export interface Reservation {
  user: Usage;
  global: Usage;
}

/**
 * The counting rule, pure: would adding `cost` stay within every limit? Returns the new totals or throws `quota` (429).
 * The user's own limits are checked first, so a person near their limit hears about theirs, not the global one.
 */
export function applyReservation(user: Usage, global: Usage, cost: Cost, limits: Limits, now: Date): Reservation {
  const resetsAt = nextReset(now);
  const check = (scope: NonNullable<ErrorExtras["scope"]>, used: number, add: number, limit: number) => {
    if (add <= 0 || used + add <= limit) return;
    const who = scope.startsWith("user") ? "You have" : "Everyone has";
    const what = scope.endsWith("seconds") ? "audio time" : "requests";
    throw new ProxyError("quota", 429, `${who} used up today's ${what}. It resets at midnight UTC.`, {
      scope,
      limit,
      used,
      resetsAt: resetsAt.toISOString(),
      retryAfterSeconds: Math.max(1, Math.ceil((resetsAt.getTime() - now.getTime()) / 1000)),
    });
  };
  check("user_requests", user.requests, cost.requests, limits.userRequestsPerDay);
  check("user_seconds", user.seconds, cost.seconds, limits.userSecondsPerDay);
  check("global_requests", global.requests, cost.requests, limits.globalRequestsPerDay);
  check("global_seconds", global.seconds, cost.seconds, limits.globalSecondsPerDay);
  return {
    user: { seconds: user.seconds + cost.seconds, requests: user.requests + cost.requests },
    global: { seconds: global.seconds + cost.seconds, requests: global.requests + cost.requests },
  };
}

export interface QuotaStore {
  /** Atomically check and count `cost` for `uid` and for the whole app. Throws `quota` when a limit would be passed. */
  reserve(uid: string, cost: Cost, limits: Limits, now: Date): Promise<Reservation>;
  /** Give a reservation back (the Hypa call failed). Never goes below zero. */
  release(uid: string, cost: Cost, now: Date): Promise<void>;
}

const usageOf = (data?: FirebaseFirestore.DocumentData): Usage => ({
  seconds: Number(data?.seconds) || 0,
  requests: Number(data?.requests) || 0,
});

/** Counts in Firestore: usage/{uid}/days/{yyyy-mm-dd} per user, usage_global/{yyyy-mm-dd} for the app. */
export class FirestoreQuotaStore implements QuotaStore {
  constructor(private readonly db: Firestore) {}

  private refs(uid: string, now: Date) {
    const day = dayKey(now);
    return {
      user: this.db.collection("usage").doc(uid).collection("days").doc(day),
      global: this.db.collection("usage_global").doc(day),
    };
  }

  reserve(uid: string, cost: Cost, limits: Limits, now: Date) {
    const { user, global } = this.refs(uid, now);
    const meta = {
      day: dayKey(now),
      expireAt: Timestamp.fromMillis(now.getTime() + USAGE_RETENTION_DAYS * 86_400_000),
    };
    return this.db.runTransaction(async (tx) => {
      const [u, g] = await Promise.all([tx.get(user), tx.get(global)]);
      const next = applyReservation(usageOf(u.data()), usageOf(g.data()), cost, limits, now);
      tx.set(user, { ...next.user, ...meta }, { merge: true });
      tx.set(global, { ...next.global, ...meta }, { merge: true });
      return next;
    });
  }

  async release(uid: string, cost: Cost, now: Date) {
    const { user, global } = this.refs(uid, now);
    await this.db.runTransaction(async (tx) => {
      for (const ref of [user, global]) {
        const snap = await tx.get(ref);
        if (!snap.exists) continue;
        const used = usageOf(snap.data());
        tx.set(
          ref,
          { seconds: Math.max(0, used.seconds - cost.seconds), requests: Math.max(0, used.requests - cost.requests) },
          { merge: true },
        );
      }
    });
  }
}
