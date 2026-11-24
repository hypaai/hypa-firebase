import { describe, expect, it } from "vitest";
import { MAX_ASR_BASE64_CHARS } from "../src/config";
import { ProxyError } from "../src/api/errors";
import { handleRequest } from "../src/api/handler";
import { routePath } from "../src/routes";
import { audioSeconds, wavSeconds } from "../src/utils/audio";
import { deps, fakeHypa, LIMITS, request, wav } from "./helpers";

const err = (r: { body: any }) => r.body.error;

describe("who may call", () => {
  it("401 without sign-in and never reaches Hypa or the quota", async () => {
    const d = deps();
    const r = await handleRequest(
      request("/hypa/tts", { text: "a", language: "Yoruba" }, { "x-firebase-appcheck": "good-ac" }),
      d,
    );
    expect([r.status, err(r).code]).toEqual([401, "unauthenticated"]);
    expect((d.hypa as ReturnType<typeof fakeHypa>).calls).toEqual([]);
    expect(d.quota.global.requests).toBe(0);
  });

  it("401 without App Check, 403 with a forged one", async () => {
    const a = await handleRequest(request("/hypa/tts", {}, { authorization: "Bearer good-id" }), deps());
    expect([a.status, err(a).code]).toEqual([401, "appcheck"]);
    const b = await handleRequest(
      request("/hypa/tts", {}, { authorization: "Bearer good-id", "x-firebase-appcheck": "forged" }),
      deps(),
    );
    expect([b.status, err(b).code]).toEqual([403, "appcheck"]);
  });

  it("answers 404 and 405 before auth for unknown routes and methods", async () => {
    expect((await handleRequest(request("/hypa/nope", {}), deps())).status).toBe(404);
    expect((await handleRequest(request("/hypa/tts", {}, undefined, "GET"), deps())).status).toBe(405);
  });
});

describe("routing", () => {
  it("accepts the Hosting path, the emulator/direct path and a trailing slash", () => {
    expect(["/hypa/tts", "/tts", "/hypa/tts/", "/hypa/asr"].map(routePath)).toEqual(["/tts", "/tts", "/tts", "/asr"]);
  });
});

describe("/hypa/tts", () => {
  it("speaks, returns which model and voice answered, and the caller's usage", async () => {
    const d = deps();
    const r = await handleRequest(request("/hypa/tts", { text: "Bawo ni", language: "Yoruba" }), d);
    expect(r.status).toBe(200);
    expect(r.body).toMatchObject({
      audioBase64: "YXVkaW8=",
      mime: "audio/mpeg",
      model: "auto-1",
      voice: "Ife",
      route: { provider: "hypaai", model: "auto-1", voice: "Ife" },
      usage: {
        secondsUsed: 1,
        secondsLimit: LIMITS.userSecondsPerDay,
        requestsUsed: 1,
        requestsLimit: LIMITS.userRequestsPerDay,
        resetsAt: "2026-11-25T00:00:00.000Z",
      },
    });
  });

  it("charges audio seconds by text length (15 chars/s, rounded up)", async () => {
    const d = deps();
    await handleRequest(request("/hypa/tts", { text: "x".repeat(31), language: "Yoruba" }), d);
    expect(d.quota.users.get("user-1")).toEqual({ seconds: 3, requests: 1 });
  });

  it("rejects bad or oversized input before spending quota", async () => {
    const d = deps();
    expect((await handleRequest(request("/hypa/tts", { language: "Yoruba" }), d)).status).toBe(400);
    expect((await handleRequest(request("/hypa/tts", { text: "x".repeat(1001), language: "Yoruba" }), d)).status).toBe(
      413,
    );
    expect((await handleRequest(request("/hypa/tts", ["no"]), d)).status).toBe(400);
    expect(d.quota.global.requests).toBe(0);
  });
});

describe("quota enforcement", () => {
  it("returns a quota error the app can show once the user's requests run out", async () => {
    const d = deps();
    for (let i = 0; i < LIMITS.userRequestsPerDay; i++) {
      expect(
        (await handleRequest(request("/hypa/translate", { text: "hi", from: "English", to: "Yoruba" }), d)).status,
      ).toBe(200);
    }
    const r = await handleRequest(request("/hypa/translate", { text: "hi", from: "English", to: "Yoruba" }), d);
    expect(r.status).toBe(429);
    expect(r.headers?.["retry-after"]).toBe(String(14 * 3600));
    expect(err(r)).toMatchObject({
      code: "quota",
      scope: "user_requests",
      limit: 5,
      used: 5,
      resetsAt: "2026-11-25T00:00:00.000Z",
    });
  });

  it("gives the quota back when Hypa fails, so an outage does not use up someone's day", async () => {
    const d = deps({
      hypa: fakeHypa({
        tts: async () => {
          throw new ProxyError("upstream", 502, "down", { reason: "unavailable" });
        },
      }),
    });
    const r = await handleRequest(request("/hypa/tts", { text: "x".repeat(45), language: "Yoruba" }), d);
    expect([r.status, err(r).code]).toEqual([502, "upstream"]);
    expect(d.quota.users.get("user-1")).toEqual({ seconds: 0, requests: 0 });
    expect(d.quota.global).toEqual({ seconds: 0, requests: 0 });
  });

  it("turns an unexpected exception into `internal` without leaking its message", async () => {
    const d = deps({
      hypa: fakeHypa({
        chat: async () => {
          throw new Error("secret detail KEY-9");
        },
      }),
    });
    const r = await handleRequest(
      request("/hypa/chat", { language: "Yoruba", messages: [{ role: "user", content: "hi" }] }),
      d,
    );
    expect(r.status).toBe(500);
    expect(JSON.stringify(r.body)).not.toContain("KEY-9");
    expect(d.quota.users.get("user-1")).toEqual({ seconds: 0, requests: 0 });
  });
});

describe("/hypa/asr", () => {
  const send = (buf: Buffer, extra: object = {}, d = deps()) =>
    handleRequest(
      request("/hypa/asr", { audioBase64: buf.toString("base64"), mime: "audio/wav", language: "Yoruba", ...extra }),
      d,
    ).then((r) => ({ r, d }));

  it("reads the WAV duration and charges whole seconds", async () => {
    expect(wavSeconds(wav(2.5))).toBeCloseTo(2.5, 2);
    const { r, d } = await send(wav(2.5));
    expect(r.status).toBe(200);
    expect(r.body).toMatchObject({ text: "bawo ni", model: "asr-1" });
    expect(d.quota.users.get("user-1")).toEqual({ seconds: 3, requests: 1 });
  });

  it("accepts 30 s and rejects longer audio", async () => {
    expect((await send(wav(30))).r.status).toBe(200);
    const long = await send(wav(30.5));
    expect([long.r.status, err(long.r).code]).toEqual([413, "too_large"]);
  });

  it("does not let a caller under-report a long recording, nor over-claim past the cap", async () => {
    expect(audioSeconds(wav(20), 1)).toBe(20);
    expect((await send(wav(1), { durationSeconds: 45 })).r.status).toBe(413);
    expect((await send(wav(1), { durationSeconds: "x" })).r.status).toBe(400);
  });

  it("rejects audio over the byte cap and base64 over the character cap", async () => {
    const big = await send(Buffer.alloc(1024 * 1024 + 1));
    expect([big.r.status, err(big.r).code]).toEqual([413, "too_large"]);
    const r = await handleRequest(request("/hypa/asr", { audioBase64: "A".repeat(MAX_ASR_BASE64_CHARS + 1) }), deps());
    expect(r.status).toBe(413);
  });

  it("prices non-WAV audio by size when it has no readable duration", () => {
    expect(audioSeconds(Buffer.alloc(64_000))).toBe(2);
  });
});

describe("/hypa/chat and /hypa/translate", () => {
  it("answers a Yorùbá question and reports the route, charging a request but no audio seconds", async () => {
    const d = deps();
    const r = await handleRequest(
      request("/hypa/chat", { language: "Yoruba", messages: [{ role: "user", content: "Báwo ni ọjọ́ ṣe rí?" }] }),
      d,
    );
    expect(r.status).toBe(200);
    expect(r.body).toMatchObject({ text: "Ẹ n lẹ́", model: "muse", route: { provider: "meta", model: "muse" } });
    expect(d.quota.users.get("user-1")).toEqual({ seconds: 0, requests: 1 });
  });

  it("validates messages, model and max tokens", async () => {
    const bad = (body: object) => handleRequest(request("/hypa/chat", body), deps()).then((r) => r.status);
    expect(await bad({})).toBe(400);
    expect(await bad({ language: "Yoruba", messages: [{ role: "robot", content: "x" }] })).toBe(400);
    expect(await bad({ language: "Yoruba", messages: [{ role: "user", content: "x" }], model: "nonsense" })).toBe(400);
    expect(await bad({ language: "Yoruba", messages: [{ role: "user", content: "x" }], maxTokens: 0 })).toBe(400);
    expect(await bad({ language: "Yoruba", messages: [{ role: "user", content: "x".repeat(8001) }] })).toBe(413);
    expect(await bad({ language: "Yoruba", messages: Array(21).fill({ role: "user", content: "x" }) })).toBe(413);
  });

  it("passes the pinned model and caps max tokens", async () => {
    let seen: any;
    const d = deps({
      hypa: fakeHypa({
        chat: async (i) => {
          seen = i;
          return { content: "ok", route: {} };
        },
      }),
    });
    await handleRequest(
      request("/hypa/chat", {
        language: "Yoruba",
        messages: [{ role: "user", content: "x" }],
        system: "You are a storyteller.",
        model: "meta/team/muse",
        maxTokens: 99999,
      }),
      d,
    );
    expect(seen).toMatchObject({
      model: "meta/team/muse",
      maxTokens: 1024,
      messages: [
        { role: "system", content: "You are a storyteller.\n\nAnswer in Yoruba." },
        { role: "user", content: "x" },
      ],
    });
  });

  it("translates", async () => {
    const r = await handleRequest(request("/hypa/translate", { text: "hello", from: "English", to: "Yoruba" }), deps());
    expect(r.body).toMatchObject({ text: "hello", model: "mt-1" });
    expect((await handleRequest(request("/hypa/translate", { text: "hello" }), deps())).status).toBe(400);
  });
});
