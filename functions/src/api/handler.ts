import { authenticate } from "../auth";
import { routePath, routes } from "../routes";
import { nextReset } from "../utils/time";
import { badRequest, ProxyError } from "./errors";
import type { Deps, ProxyRequest, ProxyResponse } from "./types";

/** One request, start to finish: route -> sign-in -> validate -> reserve quota -> call Hypa -> respond (refunding on failure). */
export async function handleRequest(req: ProxyRequest, deps: Deps): Promise<ProxyResponse> {
  const now = deps.now ?? (() => new Date());
  const started = Date.now();
  const path = routePath(req.path);
  let uid: string | undefined;
  let refund: (() => Promise<void>) | undefined;

  try {
    const route = routes[path];
    if (!route) throw new ProxyError("not_found", 404, "Unknown endpoint.");
    if (req.method !== "POST") throw new ProxyError("method_not_allowed", 405, "Use POST.");

    const caller = await authenticate(req, deps.verifiers, deps.skipAppCheck);
    uid = caller.uid;
    if (typeof req.body !== "object" || req.body === null || Array.isArray(req.body))
      throw badRequest("Send a JSON object.");
    const { cost, run } = await route(req.body as Record<string, unknown>, deps.hypa);

    const at = now();
    const limits = await deps.limits.get();
    const { user } = await deps.quota.reserve(caller.uid, cost, limits, at);
    refund = () => deps.quota.release(caller.uid, cost, at);

    const result = await run();
    refund = undefined;
    deps.log?.info("hypa call", {
      path,
      uid,
      status: 200,
      ms: Date.now() - started,
      seconds: cost.seconds,
      route: result.route,
    });
    const usage = {
      secondsUsed: user.seconds,
      secondsLimit: limits.userSecondsPerDay,
      requestsUsed: user.requests,
      requestsLimit: limits.userRequestsPerDay,
      resetsAt: nextReset(at).toISOString(),
    };
    return { status: 200, body: { ...result, usage } };
  } catch (e) {
    await refund?.().catch((err) => deps.log?.warn("quota release failed", { uid, error: String(err) }));
    const err =
      e instanceof ProxyError ? e : new ProxyError("internal", 500, "Something went wrong on our side. Try again.");
    const fields = {
      path,
      uid,
      status: err.status,
      code: err.code,
      reason: err.extras.reason,
      scope: err.extras.scope,
      ms: Date.now() - started,
    };
    if (!(e instanceof ProxyError))
      deps.log?.error("hypa call failed", { ...fields, error: e instanceof Error ? e.name : "unknown" });
    else deps.log?.[err.status >= 500 ? "warn" : "info"]("hypa call rejected", fields);
    const headers = err.extras.retryAfterSeconds ? { "retry-after": String(err.extras.retryAfterSeconds) } : undefined;
    return { status: err.status, body: err.toBody(), headers };
  }
}
