import { describe, expect, it, vi } from "vitest";
import { mapHttpError, ProxyError } from "../src/api/errors";
import { handleRequest } from "../src/api/handler";
import { HypaHttpClient, parseToolResult } from "../src/api/hypa";
import { jsonPayloads } from "../src/utils/sse";
import { deps, request, wav } from "./helpers";

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
  return {
    seen,
    hypa: new HypaHttpClient({
      baseUrl: "https://hypa.test/",
      apiKey: () => key,
      fetchImpl,
    }),
  };
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
            audioBase64: "YXVkaW8=",
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
      arguments: { text: "Bawo", language: "Yoruba", model: "auto", format: "base64" },
    });
    expect(out.route).toEqual({ provider: "hypaai", model: "hypa/auto", voice: "Ife" });
  });

  it("resolves a voice-only request and forwards the selected voice and model", async () => {
    const c = client((_, init) => {
      const { name } = JSON.parse(String(init.body)).params;
      return new Response(
        toolOk(
          name === "list_voices"
            ? { options: [{ model: "hypa/model-1", voices: ["Ife"] }] }
            : { audioBase64: "YXVkaW8=", mime: "audio/mpeg", voice: "Ife", model: "model-1" },
        ),
      );
    });
    await c.hypa.tts({ text: "a", language: "Yoruba", voice: "ife" });
    expect(JSON.parse(String(c.seen[0].init.body)).params).toEqual({
      name: "list_voices",
      arguments: { language: "Yoruba" },
    });
    expect(JSON.parse(String(c.seen[1].init.body)).params.arguments).toEqual({
      text: "a",
      language: "Yoruba",
      model: "hypa/model-1",
      voice: "Ife",
      format: "base64",
    });
    expect(c.seen[0].init.signal).toBe(c.seen[1].init.signal);
  });

  it("honors a TTS model pin even without a voice", async () => {
    const c = client(
      () =>
        new Response(
          toolOk({
            audioBase64: "YXVkaW8=",
            mime: "audio/mpeg",
            voice: "Ife",
            model: "model-1",
          }),
        ),
    );
    const out = await c.hypa.tts({ text: "a", language: "Yoruba", model: "hypa/model-1" });
    expect(JSON.parse(String(c.seen[0].init.body)).params.arguments).toMatchObject({
      model: "hypa/model-1",
      format: "base64",
    });
    expect(out.audioBase64).toBe("YXVkaW8=");
  });

  it("rejects a voice shared by multiple models until the caller specifies one", async () => {
    const c = client(
      () =>
        new Response(
          toolOk({
            options: [
              { model: "hypa/one", voices: ["Ife"] },
              { model: "hypa/two", voices: ["Ife"] },
            ],
          }),
        ),
    );
    const err = await failure(c.hypa.tts({ text: "a", language: "Yoruba", voice: "Ife" }));
    expect(err.code).toBe("bad_request");
    expect(c.seen).toHaveLength(1);
  });
});

describe("model selection through the proxy", () => {
  const chatRequest = { messages: [{ role: "user", content: "Báwo ni?" }], language: "Yoruba" };
  const speech = { audioBase64: "YXVkaW8=", mime: "audio/mpeg", voice: "Ife" };
  const recording = { audioBase64: wav(1).toString("base64"), mime: "audio/wav", language: "Yoruba" };

  it.each([
    ["/hypa/tts", { text: "Hi", language: "English" }, "text_to_speech", speech],
    ["/hypa/asr", recording, "speech_to_text", { transcript: "hi" }],
    ["/hypa/translate", { text: "Hi", from: "English", to: "Yoruba" }, "translate_message", { translation: "hi" }],
  ])("passes omitted, Auto, and pinned models for %s", async (path, body, tool, result) => {
    for (const model of [undefined, "  ", "AUTO", "meta/team/model"]) {
      const c = client(() => new Response(toolOk({ ...result, model: "answered-model" })));
      const response = await handleRequest(request(path, { ...body, model }), deps({ hypa: c.hypa }));
      expect(response.status).toBe(200);
      expect(response.body).toMatchObject({ model: "answered-model" });
      expect(c.seen).toHaveLength(1);
      expect(JSON.parse(String(c.seen[0].init.body)).params).toMatchObject({
        name: tool,
        arguments: { model: model === "meta/team/model" ? model : "auto" },
      });
    }
  });

  it.each([undefined, "  ", "auto"])("uses the VR fallback order for chat model %s", async (model) => {
    const c = client((_, init) => {
      const { provider, model: selected } = JSON.parse(String(init.body));
      if (selected.includes("Muse-Spark")) {
        return new Response("event: message\ndata: The requested model was not found.\n\n");
      }
      if (provider === "google")
        return new Response(sse({ choices: [{ message: { content: "<think>only thinking</think>" } }] }));
      if (selected.includes("Muse-Glimmer")) return new Response("", { status: 503 });
      return new Response(sse({ choices: [{ message: { content: "Ẹ n lẹ́" } }], model: "reported-winner" }));
    });
    const d = deps({ hypa: c.hypa });
    const response = await handleRequest(request("/hypa/chat", { ...chatRequest, model }), d);
    expect(response.status).toBe(200);
    expect(response.body).toMatchObject({ text: "Ẹ n lẹ́", model: "reported-winner", route: { provider: "meta" } });
    expect(
      c.seen.map(({ init }) => {
        const body = JSON.parse(String(init.body));
        return body.provider + "/" + body.model;
      }),
    ).toEqual([
      "meta/meta-models/Muse-Spark-Open-816B-A42B:baseten",
      "google/gemini-3.5-flash",
      "meta/meta-models/Muse-Glimmer-30B:deepinfra",
      "meta/muse-wonder-1.0",
    ]);
    expect(c.seen.every(({ init }) => init.signal === c.seen[0].init.signal)).toBe(true);
    expect(d.quota.global.requests).toBe(1);
    expect(d.quota.released).toHaveLength(0);
  });

  it("stops at the first usable answer", async () => {
    const c = client(() => new Response(sse({ choices: [{ message: { content: "Done." } }] })));
    const response = await handleRequest(request("/hypa/chat", chatRequest), deps({ hypa: c.hypa }));
    expect(response.body).toMatchObject({ model: "meta-models/Muse-Spark-Open-816B-A42B:baseten" });
    expect(c.seen).toHaveLength(1);
  });

  it("preserves slashes in a pinned model and makes only one attempt even if it fails", async () => {
    const c = client(() => new Response("", { status: 503 }));
    const response = await handleRequest(
      request("/hypa/chat", { ...chatRequest, model: "meta/team/muse-wonder-1.0" }),
      deps({ hypa: c.hypa }),
    );
    expect(response.status).toBe(502);
    expect(c.seen).toHaveLength(1);
    expect(JSON.parse(String(c.seen[0].init.body))).toMatchObject({
      provider: "meta",
      model: "team/muse-wonder-1.0",
      stream: false,
    });
  });

  it.each([401, 402, 403, 422])("stops immediately for request-wide Hypa HTTP %i", async (status) => {
    const c = client(() => new Response("credentials or request rejected", { status }));
    const response = await handleRequest(request("/hypa/chat", chatRequest), deps({ hypa: c.hypa }));
    expect(response.status).toBe(status === 402 ? 429 : 502);
    expect(c.seen).toHaveLength(1);
    expect(JSON.stringify(response.body)).not.toContain("credentials");
  });

  it("stops on a budget error inside a chat event", async () => {
    const c = client(() => new Response(sse({ error: { code: "INSUFFICIENT_BALANCE" } })));
    const response = await handleRequest(request("/hypa/chat", chatRequest), deps({ hypa: c.hypa }));
    expect(response.status).toBe(429);
    expect(c.seen).toHaveLength(1);
  });

  it("refunds the single reservation when no chat model can answer", async () => {
    const c = client(() => new Response("", { status: 503 }));
    const d = deps({ hypa: c.hypa });
    const response = await handleRequest(request("/hypa/chat", chatRequest), d);
    expect(response.status).toBe(502);
    expect(c.seen).toHaveLength(4);
    expect(d.quota.global.requests).toBe(0);
    expect(d.quota.released).toEqual([{ requests: 1, seconds: 0 }]);
  });

  it("stops after the shared deadline, with one refunded reservation", async () => {
    const controller = new AbortController();
    const timeout = vi.spyOn(AbortSignal, "timeout").mockReturnValue(controller.signal);
    try {
      const c = client(() => {
        controller.abort();
        return new Response("", { status: 503 });
      });
      const d = deps({ hypa: c.hypa });
      const response = await handleRequest(request("/hypa/chat", chatRequest), d);
      expect(timeout).toHaveBeenCalledExactlyOnceWith(20_000);
      expect(response.status).toBe(504);
      expect(c.seen).toHaveLength(1);
      expect(d.quota.global.requests).toBe(0);
      expect(d.quota.released).toEqual([{ requests: 1, seconds: 0 }]);
    } finally {
      timeout.mockRestore();
    }
  });
});
