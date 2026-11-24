import type { Verifiers } from "../src/auth";
import type { HypaApi } from "../src/api/hypa";
import type { Deps, ProxyRequest } from "../src/api/types";
import type { Limits } from "../src/services/limits";
import { applyReservation, type Cost, type QuotaStore, type Usage } from "../src/services/quota";

export const LIMITS: Limits = {
  userSecondsPerDay: 60,
  userRequestsPerDay: 5,
  globalSecondsPerDay: 1000,
  globalRequestsPerDay: 100,
};

/** Valid when the token equals "good-id" / "good-ac". */
export const verifiers: Verifiers = {
  verifyIdToken: async (t) => {
    if (t !== "good-id") throw new Error("bad");
    return { uid: "user-1" };
  },
  verifyAppCheck: async (t) => {
    if (t !== "good-ac") throw new Error("bad");
    return {};
  },
};

export class MemoryQuota implements QuotaStore {
  users = new Map<string, Usage>();
  global: Usage = { seconds: 0, requests: 0 };
  released: Cost[] = [];
  async reserve(uid: string, cost: Cost, limits: Limits, now: Date) {
    const user = this.users.get(uid) ?? { seconds: 0, requests: 0 };
    const next = applyReservation(user, this.global, cost, limits, now);
    this.users.set(uid, next.user);
    this.global = next.global;
    return next;
  }
  async release(uid: string, cost: Cost) {
    const u = this.users.get(uid)!;
    this.users.set(uid, {
      seconds: Math.max(0, u.seconds - cost.seconds),
      requests: Math.max(0, u.requests - cost.requests),
    });
    this.global = {
      seconds: Math.max(0, this.global.seconds - cost.seconds),
      requests: Math.max(0, this.global.requests - cost.requests),
    };
    this.released.push(cost);
  }
}

export function fakeHypa(overrides: Partial<HypaApi> = {}): HypaApi & { calls: string[] } {
  const calls: string[] = [];
  return {
    calls,
    tts: async () => {
      calls.push("tts");
      return {
        audioBase64: "YXVkaW8=",
        mime: "audio/mpeg",
        route: { provider: "hypaai", model: "auto-1", voice: "Ife" },
      };
    },
    asr: async () => {
      calls.push("asr");
      return { transcript: "bawo ni", route: { model: "asr-1" } };
    },
    translate: async () => {
      calls.push("translate");
      return { translation: "hello", language: "Yoruba", route: { model: "mt-1" } };
    },
    chat: async () => {
      calls.push("chat");
      return { content: "Ẹ n lẹ́", route: { provider: "meta", model: "muse" } };
    },
    ...overrides,
  };
}

export function deps(over: Partial<Deps> = {}): Deps & { quota: MemoryQuota } {
  return {
    verifiers,
    quota: new MemoryQuota(),
    limits: { get: async () => LIMITS },
    hypa: fakeHypa(),
    now: () => new Date("2026-11-24T10:00:00Z"),
    ...over,
  } as Deps & { quota: MemoryQuota };
}

export function request(
  path: string,
  body: unknown,
  headers: Record<string, string> = { authorization: "Bearer good-id", "x-firebase-appcheck": "good-ac" },
  method = "POST",
): ProxyRequest {
  const h = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
  return { method, path, body, header: (n) => h[n.toLowerCase()] };
}

/** A minimal WAV: 16 kHz mono 16-bit with `seconds` of silence. */
export function wav(seconds: number, sampleRate = 16000): Buffer {
  const dataSize = Math.round(seconds * sampleRate * 2);
  const b = Buffer.alloc(44 + dataSize);
  b.write("RIFF", 0);
  b.writeUInt32LE(36 + dataSize, 4);
  b.write("WAVE", 8);
  b.write("fmt ", 12);
  b.writeUInt32LE(16, 16);
  b.writeUInt16LE(1, 20);
  b.writeUInt16LE(1, 22);
  b.writeUInt32LE(sampleRate, 24);
  b.writeUInt32LE(sampleRate * 2, 28);
  b.writeUInt16LE(2, 32);
  b.writeUInt16LE(16, 34);
  b.write("data", 36);
  b.writeUInt32LE(dataSize, 40);
  return b;
}
