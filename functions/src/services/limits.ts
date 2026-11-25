import { DEFAULT_LIMITS, LIMITS_CACHE_MS, RC_KEYS } from "../config";

export interface Limits {
  userSecondsPerDay: number;
  userRequestsPerDay: number;
  globalSecondsPerDay: number;
  globalRequestsPerDay: number;
}

export interface LimitsProvider {
  get(): Promise<Limits>;
}

/** The slice of the Admin Remote Config server template the limits need. */
export interface ConfigSource {
  load(): Promise<void>;
  getNumber(key: string): number;
}

/** A Remote Config number is kept only if it is finite and non-negative. */
export const sanitizeLimit = (value: unknown, fallback: number) =>
  typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : fallback;

/** Limits from Remote Config, cached. Falls back to the last good values, then to DEFAULT_LIMITS, when Remote Config fails. */
export class RemoteConfigLimits implements LimitsProvider {
  private cached?: { at: number; limits: Limits };

  constructor(
    private readonly source: ConfigSource,
    private readonly now: () => number = Date.now,
    private readonly onError: (e: unknown) => void = () => undefined,
  ) {}

  async get(): Promise<Limits> {
    if (this.cached && this.now() - this.cached.at < LIMITS_CACHE_MS) return this.cached.limits;
    let limits: Limits;
    try {
      await this.source.load();
      const n = (key: keyof Limits) => sanitizeLimit(this.source.getNumber(RC_KEYS[key]), DEFAULT_LIMITS[key]);
      limits = {
        userSecondsPerDay: n("userSecondsPerDay"),
        userRequestsPerDay: n("userRequestsPerDay"),
        globalSecondsPerDay: n("globalSecondsPerDay"),
        globalRequestsPerDay: n("globalRequestsPerDay"),
      };
    } catch (e) {
      this.onError(e);
      limits = this.cached?.limits ?? { ...DEFAULT_LIMITS };
    }
    this.cached = { at: this.now(), limits };
    return limits;
  }
}
