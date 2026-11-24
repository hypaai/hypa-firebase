import { describe, expect, it } from "vitest";
import { authenticate, bearerToken } from "../src/auth";
import { ProxyError } from "../src/api/errors";
import { request, verifiers } from "./helpers";

const call = (headers: Record<string, string>, skipAppCheck = false) =>
  authenticate(request("/hypa/tts", {}, headers), verifiers, skipAppCheck);
const failure = async (p: Promise<unknown>) => {
  try {
    await p;
  } catch (e) {
    return e as ProxyError;
  }
  throw new Error("expected a rejection");
};

describe("token checks", () => {
  it("accepts a valid ID token and App Check token", async () => {
    expect(await call({ authorization: "Bearer good-id", "x-firebase-appcheck": "good-ac" })).toEqual({
      uid: "user-1",
    });
  });

  it("401 without sign-in", async () => {
    const e = await failure(call({ "x-firebase-appcheck": "good-ac" }));
    expect([e.status, e.code]).toEqual([401, "unauthenticated"]);
  });

  it("401 with a bad or non-bearer ID token", async () => {
    expect((await failure(call({ authorization: "Bearer nope", "x-firebase-appcheck": "good-ac" }))).status).toBe(401);
    expect((await failure(call({ authorization: "Basic good-id", "x-firebase-appcheck": "good-ac" }))).status).toBe(
      401,
    );
  });

  it("401 without App Check", async () => {
    const e = await failure(call({ authorization: "Bearer good-id" }));
    expect([e.status, e.code]).toEqual([401, "appcheck"]);
  });

  it("403 with an invalid App Check token", async () => {
    const e = await failure(call({ authorization: "Bearer good-id", "x-firebase-appcheck": "forged" }));
    expect([e.status, e.code]).toEqual([403, "appcheck"]);
  });

  it("checks sign-in before App Check, so a probe learns nothing about App Check", async () => {
    const e = await failure(call({}));
    expect(e.code).toBe("unauthenticated");
  });

  it("parses bearer tokens case-insensitively", () => {
    expect(bearerToken({ header: () => "bearer abc" })).toBe("abc");
    expect(bearerToken({ header: () => "Bearer" })).toBeUndefined();
  });
});

describe("skipping App Check (emulator only)", () => {
  it("lets the call through without App Check, but still requires sign-in", async () => {
    expect(await call({ authorization: "Bearer good-id" }, true)).toEqual({ uid: "user-1" });
    expect((await failure(call({}, true))).status).toBe(401);
  });

  it("is off by default", async () => {
    expect((await failure(call({ authorization: "Bearer good-id" }))).code).toBe("appcheck");
  });
});
