import { badRequest, tooLarge } from "../api/errors";

type Body = Record<string, unknown>;

/** An optional text field: undefined when absent, 400 if it is not text, 413 if longer than `max`. */
export function optional(body: Body, key: string, max: number): string | undefined {
  const v = body[key];
  if (v === undefined || v === null || v === "") return undefined;
  if (typeof v !== "string") throw badRequest(`'${key}' must be a string.`);
  if (v.length > max) throw tooLarge(`'${key}' is too long (max ${max} characters).`);
  return v;
}

/** A required text field: like `optional`, but 400 when missing or blank. */
export function required(body: Body, key: string, max: number): string {
  const v = optional(body, key, max);
  if (!v?.trim()) throw badRequest(`'${key}' is required.`);
  return v;
}
