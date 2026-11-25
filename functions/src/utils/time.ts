/** UTC day, the key of a usage document: yyyy-mm-dd. */
export const dayKey = (d: Date) => d.toISOString().slice(0, 10);

/** Next UTC midnight: when today's counters start again. */
export const nextReset = (d: Date) => new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1));
