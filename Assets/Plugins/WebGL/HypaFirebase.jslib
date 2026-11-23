// C# -> Firebase JS SDK bridge (G03, docs/web-bridge.md). C# calls HypaFirebase_Call with a request id; the answer comes
// back with SendMessage to the HypaWebBridge object as JSON: { id, kind: "ok" | "err" | "chunk", value, code, message },
// and events as { id: 0, kind: "event", name, value }. firebase-bridge.js (the WebGL template) does the work.
mergeInto(LibraryManager.library, {
  $HypaBridge: {
    aborts: {},
    send: function (msg) { SendMessage("HypaWebBridge", "OnMessage", JSON.stringify(msg)); },
  },

  HypaFirebase_Call__deps: ["$HypaBridge"],
  HypaFirebase_Call: function (id, methodPtr, argsPtr) {
    var method = UTF8ToString(methodPtr), args = JSON.parse(UTF8ToString(argsPtr) || "null");
    var abort = typeof AbortController === "function" ? new AbortController() : { signal: { aborted: false }, abort: function () {} };
    HypaBridge.aborts[id] = abort;
    var ctx = {
      signal: abort.signal,
      emit: function (value) { if (HypaBridge.aborts[id]) HypaBridge.send({ id: id, kind: "chunk", value: value }); },
      event: function (name, value) { HypaBridge.send({ id: 0, kind: "event", name: name, value: value }); },
    };
    // window.hypaFirebase is the import() of firebase-bridge.js started by index.html; it rejects if the SDK failed to load.
    Promise.resolve(window.hypaFirebase)
      .then(function (bridge) {
        if (!bridge) throw { code: "bridge", message: "The Firebase bridge is not on this page." };
        return bridge.call(method, args, ctx);
      })
      .then(function (value) {
        if (!HypaBridge.aborts[id]) return; // cancelled: C# has stopped listening
        delete HypaBridge.aborts[id];
        HypaBridge.send({ id: id, kind: "ok", value: value === undefined ? null : value });
      }, function (e) {
        if (!HypaBridge.aborts[id]) return;
        delete HypaBridge.aborts[id];
        // The code and message only. Firebase errors carry no tokens, and C# logs the code alone.
        var code = (e && e.code) || (e && e.name) || "error";
        HypaBridge.send({ id: id, kind: "err", code: String(code), message: String((e && e.message) || code).slice(0, 300) });
      });
  },

  HypaFirebase_Cancel__deps: ["$HypaBridge"],
  HypaFirebase_Cancel: function (id) {
    var abort = HypaBridge.aborts[id];
    if (!abort) return;
    delete HypaBridge.aborts[id];
    abort.abort();
  },
});
