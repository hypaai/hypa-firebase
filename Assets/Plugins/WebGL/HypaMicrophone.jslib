// Browser microphone for WebGL, where Unity's Microphone class does not work (G03, used by MicListener and G10).
// getUserMedia with echo cancellation, noise suppression and auto gain on; an AudioWorklet averages the device rate down to
// 16 kHz mono, and C# pulls the samples with HypaMic_Read, the way it reads a Unity microphone clip.
mergeInto(LibraryManager.library, {
  $HypaMic: {
    state: 0, // 0 idle, 1 starting, 2 recording, -1 denied, -2 failed (see WebMicrophone.State)
    queue: [],
    queued: 0,
    stream: null,
    context: null,
    run: 0,
    MaxSamples: 16000 * 30, // keep at most 30 s if C# stops reading; the oldest audio goes first
    worklet:
      "class Down extends AudioWorkletProcessor {" +
      " constructor() { super(); this.step = sampleRate / 16000; this.acc = 0; this.sum = 0; this.n = 0; this.buf = new Float32Array(1600); this.len = 0; }" +
      " process(inputs) { var ch = inputs[0] && inputs[0][0]; if (!ch) return true;" +
      "  for (var i = 0; i < ch.length; i++) { this.sum += ch[i]; this.n++; this.acc += 1;" +
      "   if (this.acc >= this.step) { this.acc -= this.step; this.buf[this.len++] = this.sum / this.n; this.sum = 0; this.n = 0;" +
      "    if (this.len === this.buf.length) { this.port.postMessage(this.buf.slice(0)); this.len = 0; } } }" +
      "  return true; } }" +
      " registerProcessor('hypa-down-16k', Down);",
    close: function () {
      if (HypaMic.stream) HypaMic.stream.getTracks().forEach(function (t) { t.stop(); });
      if (HypaMic.context) HypaMic.context.close().catch(function () {});
      HypaMic.stream = null;
      HypaMic.context = null;
      HypaMic.queue = [];
      HypaMic.queued = 0;
    },
  },

  HypaMic_Start__deps: ["$HypaMic"],
  HypaMic_Start: function () {
    HypaMic.close();
    var run = ++HypaMic.run; // a Stop or a newer Start while the prompt is open wins
    HypaMic.state = 1;
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || typeof AudioWorkletNode !== "function") { HypaMic.state = -2; return; }
    // Created inside the tap that started recording, so the browser lets it play (resume) without another gesture.
    var context = new AudioContext();
    HypaMic.context = context;
    navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } })
      .then(function (stream) {
        if (run !== HypaMic.run) { stream.getTracks().forEach(function (t) { t.stop(); }); return; }
        HypaMic.stream = stream;
        var url = URL.createObjectURL(new Blob([HypaMic.worklet], { type: "application/javascript" }));
        return context.audioWorklet.addModule(url).then(function () {
          URL.revokeObjectURL(url);
          if (run !== HypaMic.run) return;
          var node = new AudioWorkletNode(context, "hypa-down-16k");
          node.port.onmessage = function (e) {
            if (run !== HypaMic.run) return;
            HypaMic.queue.push(e.data);
            HypaMic.queued += e.data.length;
            while (HypaMic.queued > HypaMic.MaxSamples) HypaMic.queued -= HypaMic.queue.shift().length;
          };
          context.createMediaStreamSource(stream).connect(node);
          return context.resume().then(function () { if (run === HypaMic.run) HypaMic.state = 2; });
        });
      })
      .catch(function (e) {
        if (run !== HypaMic.run) return;
        HypaMic.close();
        HypaMic.state = e && (e.name === "NotAllowedError" || e.name === "SecurityError") ? -1 : -2;
      });
  },

  HypaMic_Stop__deps: ["$HypaMic"],
  HypaMic_Stop: function () {
    HypaMic.run++;
    HypaMic.close();
    HypaMic.state = 0;
  },

  HypaMic_State__deps: ["$HypaMic"],
  HypaMic_State: function () { return HypaMic.state; },

  // Copies up to max queued samples into the C# float[] at ptr and returns how many.
  HypaMic_Read__deps: ["$HypaMic"],
  HypaMic_Read: function (ptr, max) {
    var written = 0, base = ptr >> 2;
    while (written < max && HypaMic.queue.length) {
      var chunk = HypaMic.queue[0], take = Math.min(chunk.length, max - written);
      HEAPF32.set(chunk.subarray(0, take), base + written);
      written += take;
      if (take === chunk.length) HypaMic.queue.shift(); else HypaMic.queue[0] = chunk.subarray(take);
    }
    HypaMic.queued -= written;
    return written;
  },
});
