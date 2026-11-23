import { describe, expect, it } from "vitest";
import { DEFAULT_LIMITS, LIMITS_CACHE_MS } from "../src/config";
import { ProxyError } from "../src/api/errors";
import { RemoteConfigLimits, sanitizeLimit } from "../src/services/limits";
import { applyReservation } from "../src/services/quota";
import { dayKey, nextReset } from "../src/utils/time";
import { LIMITS } from "./helpers";

const T = new Date("2026-11-24T23:59:00Z");
const zero = { seconds: 0, requests: 0 };
const quotaError = (fn: () => unknown) => {
  try {
    fn();
  } catch (e) {
    return e as ProxyError;
  }
  throw new Error("expected quota error");
};

describe("daily counting", () => {
  it("keys usage by UTC day", () => {
    expect(dayKey(new Date("2026-11-24T23:59:59Z"))).toBe("2026-11-24");
    expect(dayKey(new Date("2026-11-25T00:00:00Z"))).toBe("2026-11-25");
    expect(nextReset(T).toISOString()).toBe("2026-11-25T00:00:00.000Z");
  });

  it("adds a reservation to the user and to the global counters", () => {
    const r = applyReservation(
      { seconds: 10, requests: 1 },
      { seconds: 100, requests: 9 },
      { seconds: 5, requests: 1 },
      LIMITS,
      T,
    );
    expect(r).toEqual({ user: { seconds: 15, requests: 2 }, global: { seconds: 105, requests: 10 } });
  });

  it("allows landing exactly on a limit", () => {
    expect(() =>
      applyReservation({ seconds: 55, requests: 4 }, zero, { seconds: 5, requests: 1 }, LIMITS, T),
    ).not.toThrow();
  });

  it("rejects past the user's daily seconds with details the app can show", () => {
    const e = quotaError(() =>
      applyReservation({ seconds: 58, requests: 1 }, zero, { seconds: 5, requests: 1 }, LIMITS, T),
    );
    expect(e.status).toBe(429);
    expect(e.code).toBe("quota");
    expect(e.extras).toMatchObject({
      scope: "user_seconds",
      limit: 60,
      used: 58,
      resetsAt: "2026-11-25T00:00:00.000Z",
      retryAfterSeconds: 60,
    });
  });

  it("rejects past the user's daily requests, even for a free (0 s) call", () => {
    const e = quotaError(() =>
      applyReservation({ seconds: 0, requests: 5 }, zero, { seconds: 0, requests: 1 }, LIMITS, T),
    );
    expect(e.extras.scope).toBe("user_requests");
  });

  it("rejects past the global caps, after the user's own", () => {
    const g = quotaError(() =>
      applyReservation(zero, { seconds: 999, requests: 0 }, { seconds: 5, requests: 1 }, LIMITS, T),
    );
    expect(g.extras.scope).toBe("global_seconds");
    const both = quotaError(() =>
      applyReservation(
        { seconds: 60, requests: 0 },
        { seconds: 1000, requests: 0 },
        { seconds: 5, requests: 1 },
        LIMITS,
        T,
      ),
    );
    expect(both.extras.scope).toBe("user_seconds");
  });

  it("counts nothing when it rejects (pure: inputs untouched)", () => {
    const user = { seconds: 59, requests: 1 };
    quotaError(() => applyReservation(user, zero, { seconds: 5, requests: 1 }, LIMITS, T));
    expect(user).toEqual({ seconds: 59, requests: 1 });
  });
});

describe("limits from Remote Config", () => {
  const source = (values: Record<string, unknown>, fail = false) => {
    let loads = 0;
    return {
      get loads() {
        return loads;
      },
      load: async () => {
        loads++;
        if (fail) throw new Error("rc down");
      },
      getNumber: (k: string) => values[k] as number,
    };
  };

  it("reads the four parameters, including proxy_daily_seconds_per_user", async () => {
    const s = source({
      proxy_daily_seconds_per_user: 90,
      proxy_daily_requests_per_user: 12,
      proxy_global_daily_seconds: 5000,
      proxy_global_daily_requests: 700,
    });
    expect(await new RemoteConfigLimits(s).get()).toEqual({
      userSecondsPerDay: 90,
      userRequestsPerDay: 12,
      globalSecondsPerDay: 5000,
      globalRequestsPerDay: 700,
    });
  });

  it("falls back to defaults for junk values and when Remote Config is down", async () => {
    expect(sanitizeLimit(NaN, 7)).toBe(7);
    expect(sanitizeLimit(-1, 7)).toBe(7);
    expect(sanitizeLimit("5", 7)).toBe(7);
    expect(await new RemoteConfigLimits(source({ proxy_daily_seconds_per_user: "x" })).get()).toEqual(DEFAULT_LIMITS);
    const errors: unknown[] = [];
    expect(await new RemoteConfigLimits(source({}, true), Date.now, (e) => errors.push(e)).get()).toEqual(
      DEFAULT_LIMITS,
    );
    expect(errors).toHaveLength(1);
  });

  it("caches, then keeps the last good values if a refresh fails", async () => {
    let now = 0;
    let fail = false;
    let secs = 30;
    const s = {
      load: async () => {
        if (fail) throw new Error("down");
      },
      getNumber: (k: string) => (k === "proxy_daily_seconds_per_user" ? secs : 1),
    };
    const l = new RemoteConfigLimits(s, () => now);
    expect((await l.get()).userSecondsPerDay).toBe(30);
    secs = 99;
    now = LIMITS_CACHE_MS - 1;
    expect((await l.get()).userSecondsPerDay).toBe(30); // cached
    fail = true;
    now = LIMITS_CACHE_MS + 1;
    expect((await l.get()).userSecondsPerDay).toBe(30); // refresh failed: last good, not defaults
  });
});
