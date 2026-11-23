/**
 * Hypa answers with a JSON body or with server-sent events ("data: {...}\n\n").
 * Returns each JSON payload in order, skipping "[DONE]".
 */
export function jsonPayloads(body: string): string[] {
  const trimmed = body.trimStart();
  if (!trimmed) return [];
  if (trimmed.startsWith("{") || trimmed.startsWith("[")) return [trimmed];
  return body
    .replace(/\r\n/g, "\n")
    .split("\n\n")
    .map((event) =>
      event
        .split("\n")
        .filter((l) => l.startsWith("data:"))
        .map((l) => l.slice(5).replace(/^ /, ""))
        .join("\n"),
    )
    .filter((data) => data && data !== "[DONE]");
}
