import type { HeaderReader, Verifiers } from "../auth";
import type { LimitsProvider } from "../services/limits";
import type { Cost, QuotaStore } from "../services/quota";
import type { HypaApi } from "./hypa";

export interface ProxyRequest extends HeaderReader {
  method: string;
  path: string;
  body: unknown;
}

export interface ProxyResponse {
  status: number;
  body: unknown;
  headers?: Record<string, string>;
}

type Log = (message: string, data?: Record<string, unknown>) => void;

export interface Deps {
  verifiers: Verifiers;
  quota: QuotaStore;
  limits: LimitsProvider;
  hypa: HypaApi;
  now?: () => Date;
  skipAppCheck?: boolean;
  log?: { info: Log; warn: Log; error: Log };
}

export interface Prepared {
  cost: Cost;
  run(): Promise<Record<string, unknown>>;
}
export type Route = (body: Record<string, unknown>, hypa: HypaApi) => Prepared | Promise<Prepared>;
