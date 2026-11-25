import { describe, expect, it, vi } from "vitest";
import type { ProxyRequest } from "../src/api/types";
import { handleQuestAppCheck, verifyMetaAttestation } from "../src/services/questAttestation";

const policy = {
  appId: "1:123:android:abcdef",
  packageId: "com.example.quest",
  signingDigest: "a".repeat(64),
};

function setup() {
  let time = Date.UTC(2026, 10, 25);
  const challenges = new Map<string, { nonce: string; expiresAt: Date }>();
  const deps = {
    policy,
    challenges: {
      issue: async (uid: string, challenge: { nonce: string; expiresAt: Date }) => {
        challenges.set(uid, challenge);
      },
      consume: async (uid: string, nonce: string, now: number) => {
        const challenge = challenges.get(uid);
        if (challenge?.nonce !== nonce || challenge.expiresAt.getTime() <= now) return false;
        return challenges.delete(uid);
      },
    },
    verifyIdToken: vi.fn(async (token: string) => ({ uid: token })),
    verifyMeta: vi.fn<Parameters<typeof handleQuestAppCheck>[1]["verifyMeta"]>(),
    mint: vi.fn(async () => ({ token: "firebase-app-check", ttlMillis: 3600000 })),
    now: () => time,
  };
  const send = (path: string, body?: unknown, uid = "player", method = "POST") => {
    const req: ProxyRequest = {
      path,
      method,
      body,
      header: (name) => (name === "authorization" && uid ? "Bearer " + uid : undefined),
    };
    return handleQuestAppCheck(req, deps);
  };
  const issue = async () => {
    const response = await send("/challenge");
    expect(response.status).toBe(200);
    const { nonce } = response.body as { nonce: string };
    expect(nonce).toMatch(/^[A-Za-z0-9_-]{43}$/);
    const now = time / 1000;
    const claims = {
      request_details: { nonce, exp: now + 86400, timestamp: now },
      app_state: {
        app_integrity_state: "StoreRecognized",
        package_id: policy.packageId,
        package_cert_sha256_digest: [policy.signingDigest],
      },
      device_state: { device_integrity_state: "Advanced" },
    };
    deps.verifyMeta.mockResolvedValue(claims);
    return { body: { nonce, attestation: "meta-token" }, claims };
  };
  return {
    deps,
    send,
    issue,
    advance: (ms: number) => {
      time += ms;
    },
  };
}

describe("Quest App Check exchange", () => {
  it("requires Firebase sign-in and accepts only the documented methods and paths", async () => {
    const { send, deps } = setup();
    expect((await send("/challenge", {}, "")).status).toBe(401);
    deps.verifyIdToken.mockRejectedValueOnce(new Error("expired"));
    expect((await send("/challenge")).status).toBe(401);
    expect((await send("/challenge", {}, "player", "GET")).status).toBe(405);
    expect((await send("/unknown")).status).toBe(404);
    expect(deps.verifyMeta).not.toHaveBeenCalled();
  });

  it("consumes a valid challenge at most once even when exchanges overlap", async () => {
    const { deps, send, issue } = setup();
    const { body } = await issue();
    expect((await send("/", body, "other-player")).status).toBe(403);
    expect(deps.verifyMeta).not.toHaveBeenCalled();
    const results = await Promise.all([send("/", body), send("/", body)]);
    expect(results.map(({ status }) => status).sort()).toEqual([200, 403]);
    expect(results.find(({ status }) => status === 200)?.body).toEqual({
      token: "firebase-app-check",
      ttlMillis: 3600000,
    });
    expect(deps.verifyMeta).toHaveBeenCalledTimes(1);
    expect(deps.mint).toHaveBeenCalledExactlyOnceWith(policy.appId);
    expect((await send("/", body)).status).toBe(403);
  });

  it("rejects expired challenges before calling Meta", async () => {
    const { deps, send, issue, advance } = setup();
    const { body } = await issue();
    advance(300000);
    expect((await send("/", body)).status).toBe(403);
    expect(deps.verifyMeta).not.toHaveBeenCalled();
    expect(deps.mint).not.toHaveBeenCalled();
  });

  it("rejects a mismatched or expired attestation, unrelated app or signer, and insufficient integrity", async () => {
    const { deps, send, issue } = setup();
    const invalid: Array<
      (claims: Awaited<ReturnType<typeof issue>>["claims"]) => Awaited<ReturnType<typeof deps.verifyMeta>>
    > = [
      () => undefined,
      (claims) => ({ ...claims, request_details: { ...claims.request_details, nonce: "other" } }),
      (claims) => ({ ...claims, request_details: { ...claims.request_details, exp: 1 } }),
      (claims) => ({ ...claims, app_state: { ...claims.app_state, package_id: "com.example.other" } }),
      (claims) => ({ ...claims, app_state: { ...claims.app_state, package_cert_sha256_digest: ["b".repeat(64)] } }),
      (claims) => ({ ...claims, app_state: { ...claims.app_state, app_integrity_state: "NotRecognized" } }),
      (claims) => ({ ...claims, device_state: { device_integrity_state: "Basic" } }),
      (claims) => ({ ...claims, device_ban: { is_banned: true } }),
    ];
    for (const makeInvalid of invalid) {
      const { body, claims } = await issue();
      deps.verifyMeta.mockResolvedValueOnce(makeInvalid(claims));
      expect((await send("/", body)).status).toBe(403);
    }
    expect(deps.mint).not.toHaveBeenCalled();
    const { body } = await issue();
    expect((await send("/", body)).status).toBe(200);
  });

  it("only reads claims returned by Meta's verification endpoint", async () => {
    const claims = { device_state: { device_integrity_state: "Advanced" } };
    const encoded = Buffer.from(JSON.stringify(claims)).toString("base64url");
    const fetchMeta = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ data: [{ message: "success", claims: encoded }] }))
      .mockResolvedValueOnce(Response.json({ data: [{ message: "invalid signature", claims: encoded }] }));
    await expect(verifyMetaAttestation("attestation", "credential", fetchMeta)).resolves.toEqual(claims);
    const url = new URL(String(fetchMeta.mock.calls[0][0]));
    expect(url.origin + url.pathname).toBe("https://graph.oculus.com/platform_integrity/verify");
    expect(url.searchParams.get("token")).toBe("attestation");
    expect(url.searchParams.get("access_token")).toBe("credential");
    expect(fetchMeta.mock.calls[0][1]?.redirect).toBe("error");
    await expect(verifyMetaAttestation("attestation", "credential", fetchMeta)).resolves.toBeUndefined();
  });
});
