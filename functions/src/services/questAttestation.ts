import { createHash, randomBytes } from "node:crypto";
import type { Firestore } from "firebase-admin/firestore";
import { bearerToken } from "../auth";
import { badRequest, ProxyError, unauthenticated, upstream } from "../api/errors";
import type { ProxyRequest, ProxyResponse } from "../api/types";

const challengeTtlMs = 5 * 60_000;
const metaVerificationUrl = "https://graph.oculus.com/platform_integrity/verify";

interface Challenge {
  nonce: string;
  expiresAt: Date;
}

interface Challenges {
  issue(uid: string, challenge: Challenge): Promise<void>;
  consume(uid: string, nonce: string, now: number): Promise<boolean>;
}

export class FirestoreQuestChallenges implements Challenges {
  constructor(private readonly db: Firestore) {}

  private ref(uid: string) {
    const id = createHash("sha256").update(uid).digest("hex");
    return this.db.collection("questAppCheckChallenges").doc(id);
  }

  async issue(uid: string, challenge: Challenge) {
    await this.ref(uid).set(challenge);
  }

  private valid(data: FirebaseFirestore.DocumentData | undefined, nonce: string, now: number) {
    return data?.nonce === nonce && data.expiresAt.toMillis() > now;
  }

  async consume(uid: string, nonce: string, now: number) {
    const ref = this.ref(uid);
    return this.db.runTransaction(async (transaction) => {
      if (!this.valid((await transaction.get(ref)).data(), nonce, now)) return false;
      transaction.delete(ref);
      return true;
    });
  }
}

interface MetaClaims {
  request_details?: { exp?: number; timestamp?: number; nonce?: string };
  app_state?: { app_integrity_state?: string; package_id?: string; package_cert_sha256_digest?: unknown };
  device_state?: { device_integrity_state?: string };
  device_ban?: { is_banned?: boolean };
}

export interface QuestPolicy {
  appId: string;
  packageId: string;
  signingDigest: string;
}

interface QuestDeps {
  policy: QuestPolicy;
  challenges: Challenges;
  verifyIdToken(token: string): Promise<{ uid: string }>;
  verifyMeta(token: string): Promise<MetaClaims | undefined>;
  mint(appId: string): Promise<{ token: string; ttlMillis: number }>;
  now?: () => number;
}

export function questPolicy(env = process.env): QuestPolicy {
  const appId = env.QUEST_FIREBASE_APP_ID?.trim();
  const packageId = env.QUEST_ANDROID_PACKAGE_ID?.trim();
  const signingDigest = env.QUEST_ANDROID_SIGNING_SHA256?.replace(/:/g, "").trim().toLowerCase();
  if (
    !appId ||
    !/^1:\d+:android:[a-f0-9]+$/i.test(appId) ||
    !packageId ||
    !/^[a-z]\w*(?:\.[a-z]\w*)+$/i.test(packageId) ||
    !signingDigest ||
    !/^[a-f0-9]{64}$/.test(signingDigest)
  ) {
    throw new ProxyError("internal", 500, "Quest App Check is not configured.");
  }
  return { appId, packageId, signingDigest };
}

export async function verifyMetaAttestation(
  attestation: string,
  accessToken: string,
  fetchImpl: typeof fetch = fetch,
): Promise<MetaClaims | undefined> {
  if (!accessToken) throw new ProxyError("internal", 500, "Quest App Check is not configured.");
  const url = new URL(metaVerificationUrl);
  url.searchParams.set("token", attestation);
  url.searchParams.set("access_token", accessToken);

  try {
    const response = await fetchImpl(url, { redirect: "error", signal: AbortSignal.timeout(8_000) });
    if (response.status >= 500 || response.status === 429) {
      throw upstream("Meta verification is temporarily unavailable.", 503, "unavailable", true);
    }
    if (!response.ok) return undefined;
    const payload = (await response.json()) as { data?: { message?: string; claims?: string }[] };
    const first = payload?.data?.[0];
    if (first?.message !== "success" || typeof first.claims !== "string") return undefined;
    return JSON.parse(Buffer.from(first.claims, "base64url").toString("utf8")) as MetaClaims;
  } catch (error) {
    if (error instanceof ProxyError) throw error;
    if (error instanceof SyntaxError) return undefined;
    throw upstream("Meta verification is temporarily unavailable.", 503, "unavailable", true);
  }
}

function accepted(claims: MetaClaims | undefined, nonce: string, policy: QuestPolicy, now: number) {
  const request = claims?.request_details;
  const app = claims?.app_state;
  const seconds = now / 1000;
  const certs = app?.package_cert_sha256_digest;
  return (
    request?.nonce === nonce &&
    typeof request.exp === "number" &&
    request.exp > seconds &&
    typeof request.timestamp === "number" &&
    request.timestamp >= seconds - challengeTtlMs / 1000 &&
    request.timestamp <= seconds + 30 &&
    app?.package_id === policy.packageId &&
    app.app_integrity_state === "StoreRecognized" &&
    Array.isArray(certs) &&
    certs.some((cert) => typeof cert === "string" && cert.toLowerCase() === policy.signingDigest) &&
    claims?.device_state?.device_integrity_state === "Advanced" &&
    claims.device_ban?.is_banned !== true
  );
}

function denied() {
  return new ProxyError("app_check", 403, "Quest attestation was not accepted. Request a new challenge.");
}

export async function handleQuestAppCheck(req: ProxyRequest, deps: QuestDeps): Promise<ProxyResponse> {
  try {
    const path = req.path.replace(/\/+$/, "") || "/";
    if (path !== "/" && path !== "/challenge") throw new ProxyError("not_found", 404, "Unknown endpoint.");
    if (req.method !== "POST") throw new ProxyError("method_not_allowed", 405, "Use POST.");

    const idToken = bearerToken(req);
    if (!idToken) throw unauthenticated("Sign in before requesting Quest App Check.");
    let uid: string;
    try {
      ({ uid } = await deps.verifyIdToken(idToken));
    } catch {
      throw unauthenticated("The sign-in token is invalid or expired.");
    }
    if (!uid) throw unauthenticated("The sign-in token has no user.");

    const policy = deps.policy;
    const now = deps.now ?? Date.now;
    if (path === "/challenge") {
      const nonce = randomBytes(32).toString("base64url");
      const expiresAt = new Date(now() + challengeTtlMs);
      await deps.challenges.issue(uid, { nonce, expiresAt });
      return { status: 200, body: { nonce, expiresAt: expiresAt.toISOString() } };
    }

    const { nonce, attestation } = (req.body ?? {}) as Record<string, unknown>;
    if (
      typeof nonce !== "string" ||
      !/^[A-Za-z0-9_-]{43}$/.test(nonce) ||
      typeof attestation !== "string" ||
      !attestation.trim() ||
      attestation.length > 16_384
    ) {
      throw badRequest("Send the challenge nonce and Meta attestation token.");
    }

    if (!(await deps.challenges.consume(uid, nonce, now()))) throw denied();
    const claims = await deps.verifyMeta(attestation);
    if (!accepted(claims, nonce, policy, now())) {
      throw denied();
    }

    return { status: 200, body: await deps.mint(policy.appId) };
  } catch (error) {
    const failure = error instanceof ProxyError ? error : new ProxyError("internal", 500, "Quest App Check failed.");
    return { status: failure.status, body: failure.toBody() };
  }
}
