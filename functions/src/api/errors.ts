export type ErrorCode =
  | "unauthenticated"
  | "app_check"
  | "quota"
  | "bad_request"
  | "too_large"
  | "method_not_allowed"
  | "not_found"
  | "upstream"
  | "internal";

export interface ErrorExtras {
  scope?: "user_seconds" | "user_requests" | "global_seconds" | "global_requests" | "provider_balance";
  limit?: number;
  used?: number;
  resetsAt?: string;
  retryAfterSeconds?: number;
  reason?: "timeout" | "unavailable" | "rejected" | "invalid_response" | "tool_error" | "model_unavailable";
  retryable?: boolean;
}

export class ProxyError extends Error {
  constructor(
    readonly code: ErrorCode,
    readonly status: number,
    message: string,
    readonly extras: ErrorExtras = {},
  ) {
    super(message);
  }
  toBody() {
    return { error: { code: this.code, message: this.message, ...this.extras } };
  }
}

export const unauthenticated = (message: string) => new ProxyError("unauthenticated", 401, message);
export const badRequest = (message: string) => new ProxyError("bad_request", 400, message);
export const tooLarge = (message: string) => new ProxyError("too_large", 413, message);

// ----- Hypa failures -> `upstream` (the app never sees Hypa's own error text) -----

export const upstream = (
  message: string,
  status: number,
  reason: NonNullable<ErrorExtras["reason"]>,
  retryable: boolean,
) => new ProxyError("upstream", status, message, { reason, retryable });

export const timeoutError = () => upstream("Hypa took too long to answer. Try again.", 504, "timeout", true);

export const providerBalanceError = () =>
  new ProxyError("quota", 429, "The Hypa service budget is exhausted. Try again later.", {
    scope: "provider_balance",
    retryAfterSeconds: 300,
  });

export const isTimeout = (e: unknown) => e instanceof Error && (e.name === "TimeoutError" || e.name === "AbortError");

/** A Hypa HTTP failure -> our error. A 401/403 means our key is wrong: the app only hears "unavailable". */
export function mapHttpError(status: number, body: string): ProxyError {
  if (status === 402 || /INSUFFICIENT_BALANCE/.test(body)) return providerBalanceError();
  if (status === 400 || status === 404 || status === 422)
    return upstream("Hypa rejected the request.", 502, "rejected", false);
  if (status === 429) return upstream("Hypa is busy. Try again in a moment.", 503, "unavailable", true);
  if (status >= 500) return upstream("Hypa had a problem. Try again in a moment.", 502, "unavailable", true);
  return upstream("The Hypa service is unavailable.", 502, "unavailable", false);
}

/** Some chat providers deliver plain or structured errors inside a successful SSE response. */
export function chatPayloadError(payload: string): ProxyError | undefined {
  if (/\bINSUFFICIENT_BALANCE\b/i.test(payload)) return providerBalanceError();
  if (/\bmodel_(?:not_found|unavailable)\b|\bmodel (?:was not found|does not exist|may not exist)\b/i.test(payload)) {
    return upstream("The requested Hypa chat model is unavailable.", 502, "model_unavailable", false);
  }
  return undefined;
}

export function mapChatHttpError(status: number, body: string): ProxyError {
  const error = mapHttpError(status, body);
  if (error.code === "quota" || status === 401 || status === 403) return error;
  return chatPayloadError(body) ?? error;
}
