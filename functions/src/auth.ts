import { ProxyError, unauthenticated } from "./api/errors";

export interface Verifiers {
  verifyIdToken(token: string): Promise<{ uid: string }>;
  verifyAppCheck(token: string): Promise<unknown>;
}

export interface HeaderReader {
  header(name: string): string | undefined;
}

export interface Caller {
  uid: string;
}

export function bearerToken(req: HeaderReader): string | undefined {
  const raw = req.header("authorization");
  const match = raw ? /^Bearer\s+(\S+)\s*$/i.exec(raw) : null;
  return match?.[1];
}

export async function authenticate(req: HeaderReader, verifiers: Verifiers, skipAppCheck = false): Promise<Caller> {
  const idToken = bearerToken(req);
  if (!idToken) throw unauthenticated("Sign in first: send the Firebase ID token as 'Authorization: Bearer <token>'.");

  let uid: string;
  try {
    ({ uid } = await verifiers.verifyIdToken(idToken));
  } catch {
    throw unauthenticated("The sign-in token is invalid or expired. Sign in again.");
  }
  if (!uid) throw unauthenticated("The sign-in token has no user.");

  if (!skipAppCheck) {
    const appCheck = req.header("x-firebase-appcheck");
    if (!appCheck) throw new ProxyError("app_check", 401, "Missing App Check token ('X-Firebase-AppCheck').");
    try {
      await verifiers.verifyAppCheck(appCheck);
    } catch {
      throw new ProxyError("app_check", 403, "The App Check token is invalid or expired.");
    }
  }
  return { uid };
}
