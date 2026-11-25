import { describe, expect, it } from "vitest";
import { mapHttpError, ProxyError } from "../src/api/errors";
import { HypaHttpClient, parseToolResult } from "../src/api/hypa";
import { jsonPayloads } from "../src/utils/sse";

const sse = (o: unknown) => `event: message\ndata: ${JSON.stringify(o)}\n\n`;
const toolOk = (structuredContent: unknown) => sse({ jsonrpc: "2.0", id: 1, result: { structuredContent } });
const failure = async (p: Promise<unknown>) => {
  try {
    await p;
  } catch (e) {
    return e as ProxyError;
  }
  throw new Error("expected a rejection");
};

function client(respond: (url: string, init: RequestInit) => Response | Promise<Response> | Error, key = "KEY-123456") {
  const seen: { url: string; init: RequestInit }[] = [];
  const fetchImpl = (async (url: string | URL, init: RequestInit) => {
    seen.push({ url: String(url), init });
    const r = await respond(String(url), init);
    if (r instanceof Error) throw r;
    return r;
  }) as typeof fetch;
  return { seen, hypa: new HypaHttpClient({ baseUrl: "https://hypa.test/", apiKey: () => key, fetchImpl }) };
}

describe("parsing", () => {
  it("reads SSE and plain JSON bodies, skipping [DONE]", () => {
    expect(jsonPayloads('event: message\ndata: {"a":1}\n\ndata: [DONE]\n\n')).toEqual(['{"a":1}']);
    expect(jsonPayloads(' {"a":1}')).toEqual(['{"a":1}']);
    expect(jsonPayloads("")).toEqual([]);
  });

  it("returns the tool's structured output", () => {
    expect(parseToolResult(toolOk({ translation: "x" }), "translate_message")).toEqual({ translation: "x" });
  });

  it("falls back to text content", () => {
    const body = sse({ result: { content: [{ type: "text", text: '{"transcript":"hi"}' }] } });
    expect(parseToolResult(body, "speech_to_text")).toEqual({ transcript: "hi" });
  });
});

describe("error mapping", () => {
  const upstream = (status: number, body = "") => {
    const e = mapHttpError(status, body);
    return [e.code, e.status, e.extras.reason, e.extras.retryable];
  };

  it("maps Hypa failures to `upstream`", () => {
    expect(upstream(500)).toEqual(["upstream", 502, "unavailable", true]);
    expect(upstream(503)).toEqual(["upstream", 502, "unavailable", true]);
    expect(upstream(429)).toEqual(["upstream", 503, "unavailable", true]);
    expect(upstream(400)).toEqual(["upstream", 502, "rejected", false]);
    expect(upstream(401)).toEqual(["upstream", 502, "unavailable", false]); // our key is wrong: the app just sees "unavailable"
  });

  it("maps an exhausted Hypa balance to `quota`, not `upstream`", () => {
    const e = mapHttpError(402, "");
    expect([e.code, e.status, e.extras.scope]).toEqual(["quota", 429, "provider_balance"]);
    expect(() =>
      parseToolResult(
        sse({ result: { isError: true, structuredContent: { code: "INSUFFICIENT_BALANCE", error: "x" } } }),
        "text_to_speech",
      ),
    ).toThrow(/budget/);
  });

  it("maps tool errors, JSON-RPC errors and junk to `upstream`", () => {
    const run = (body: string) => {
      try {
        parseToolResult(body, "t");
      } catch (e) {
        return (e as ProxyError).extras.reason;
      }
      return undefined;
    };
    expect(run(sse({ result: { isError: true, structuredContent: { error: "no such language" } } }))).toBe(
      "tool_error",
    );
    expect(run(sse({ error: { code: -32602, message: "bad" } }))).toBe("tool_error");
    expect(run("not json")).toBe("invalid_response");
  });

  it("maps a timeout to `upstream` 504 and a network failure to `upstream` 502", async () => {
    const t = client(() => Object.assign(new Error("t"), { name: "TimeoutError" }));
    const e = await failure(t.hypa.translate({ text: "a", targetLang: "Yoruba" }));
    expect([e.code, e.status, e.extras.reason]).toEqual(["upstream", 504, "timeout"]);
    const n = client(() => new TypeError("fetch failed"));
    expect((await failure(n.hypa.translate({ text: "a", targetLang: "Yoruba" }))).status).toBe(502);
  });

  it("never lets the key appear in an error", async () => {
    const c = client(() => new Response("bad key KEY-123456", { status: 401 }));
    const e = await failure(c.hypa.tts({ text: "a", language: "Yoruba" }));
    expect(JSON.stringify(e.toBody())).not.toContain("KEY-123456");
  });
});

describe("calls", () => {
  it("sends the key only in x-api-key and asks for model auto", async () => {
    const c = client(
      () =>
        new Response(
          toolOk({
            audioUrl: "https://a/b.mp3",
            mime: "audio/mpeg",
            voice: "Ife",
            model: "hypa/auto",
            provider: "hypaai",
          }),
        ),
    );
    const out = await c.hypa.tts({ text: "Bawo", language: "Yoruba" });
    const sent = c.seen[0];
    expect(sent.url).toBe("https://hypa.test/v2/mcp");
    expect((sent.init.headers as Record<string, string>)["x-api-key"]).toBe("KEY-123456");
    expect(String(sent.init.body)).not.toContain("KEY-123456");
    expect(JSON.parse(String(sent.init.body)).params).toEqual({
      name: "text_to_speech",
      arguments: { text: "Bawo", language: "Yoruba", model: "auto" },
    });
    expect(out.route).toEqual({ provider: "hypaai", model: "hypa/auto", voice: "Ife" });
  });

  it("sends a named voice only with its model", async () => {
    const c = client(() => new Response(toolOk({ audioUrl: "https://a/b.mp3" })));
    await c.hypa.tts({ text: "a", language: "Yoruba", voice: "Ife" }); // no model: voice dropped
    await c.hypa.tts({ text: "a", language: "Yoruba", voice: "Ife", model: "m1" });
    expect(JSON.parse(String(c.seen[0].init.body)).params.arguments).toEqual({
      text: "a",
      language: "Yoruba",
      model: "auto",
    });
    expect(JSON.parse(String(c.seen[1].init.body)).params.arguments).toEqual({
      text: "a",
      language: "Yoruba",
      model: "m1",
      voice: "Ife",
    });
  });

  it("downloads the audio on the server for inline TTS, https only, without the key", async () => {
    const c = client((url) =>
      url.endsWith("/v2/mcp")
        ? new Response(toolOk({ audioUrl: "https://cdn.test/a.mp3", mime: "audio/mpeg" }))
        : new Response(Buffer.from("AUDIO")),
    );
    const out = await c.hypa.tts({ text: "a", language: "Yoruba", inline: true });
    expect(out.audioBase64).toBe(Buffer.from("AUDIO").toString("base64"));
    expect(out.audioUrl).toBeUndefined();
    expect(JSON.stringify(c.seen[1].init ?? {})).not.toContain("KEY-123456");
    const bad = client(() => new Response(toolOk({ audioUrl: "http://evil.test/a.mp3" })));
    expect((await failure(bad.hypa.tts({ text: "a", language: "Yoruba", inline: true }))).code).toBe("upstream");
  });

  it("chat: auto routes by provider, a pinned model is split like the Unity client", async () => {
    const reply = () =>
      new Response(JSON.stringify({ choices: [{ message: { content: "Ẹ n lẹ́" } }], model: "muse", provider: "meta" }));
    const c = client(reply);
    const out = await c.hypa.chat({ messages: [{ role: "user", content: "Báwo ni?" }], maxTokens: 100 });
    expect(c.seen[0].url).toBe("https://hypa.test/v2/developer/chat");
    expect(JSON.parse(String(c.seen[0].init.body))).toMatchObject({ provider: "auto", stream: false, max_tokens: 100 });
    expect(out).toEqual({ content: "Ẹ n lẹ́", route: { provider: "meta", model: "muse" } });
    await c.hypa.chat({ messages: [{ role: "user", content: "x" }], model: "meta/muse-wonder-1.0", maxTokens: 10 });
    expect(JSON.parse(String(c.seen[1].init.body))).toMatchObject({ provider: "meta", model: "muse-wonder-1.0" });
  });

  it("rejects an empty chat answer as `upstream`", async () => {
    const c = client(() => new Response(JSON.stringify({ choices: [{ message: { content: " " } }] })));
    expect(
      (await failure(c.hypa.chat({ messages: [{ role: "user", content: "x" }], maxTokens: 10 }))).extras.reason,
    ).toBe("invalid_response");
  });
});
