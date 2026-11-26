// Decodes compressed TTS audio (MP3/OGG) in WebGL via the browser's Web Audio API, avoiding UnityWebRequest data: URI hangs.
mergeInto(LibraryManager.library, {
  $HypaAudio: {
    ctx: null,
    nextId: 1,
    jobs: {},
  },

  HypaAudio_Decode__deps: ["$HypaAudio"],
  HypaAudio_Decode: function (ptr, len) {
    var id = HypaAudio.nextId++;
    var job = { state: 0, samples: null, channels: 0, rate: 0 };
    HypaAudio.jobs[id] = job;
    try {
      var Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) { job.state = -1; return id; }
      if (!HypaAudio.ctx) HypaAudio.ctx = new Ctx();
      var copy = HEAPU8.slice(ptr, ptr + len).buffer;
      HypaAudio.ctx.decodeAudioData(copy).then(function (decoded) {
        if (!HypaAudio.jobs[id]) return;
        var ch = decoded.numberOfChannels || 1;
        var frames = decoded.length || 0;
        var out = new Float32Array(frames * ch);
        for (var c = 0; c < ch; c++) {
          var src = decoded.getChannelData(c);
          for (var i = 0; i < frames; i++) out[i * ch + c] = src[i];
        }
        job.samples = out;
        job.channels = ch;
        job.rate = decoded.sampleRate || 24000;
        job.state = frames > 0 ? 1 : -1;
      }, function () {
        if (HypaAudio.jobs[id]) job.state = -1;
      });
    } catch (e) {
      job.state = -1;
    }
    return id;
  },

  HypaAudio_Status__deps: ["$HypaAudio"],
  HypaAudio_Status: function (id) {
    var job = HypaAudio.jobs[id];
    return job ? job.state : -1;
  },

  HypaAudio_Channels__deps: ["$HypaAudio"],
  HypaAudio_Channels: function (id) {
    var job = HypaAudio.jobs[id];
    return job ? job.channels : 0;
  },

  HypaAudio_Rate__deps: ["$HypaAudio"],
  HypaAudio_Rate: function (id) {
    var job = HypaAudio.jobs[id];
    return job ? job.rate : 0;
  },

  HypaAudio_Length__deps: ["$HypaAudio"],
  HypaAudio_Length: function (id) {
    var job = HypaAudio.jobs[id];
    return job && job.samples ? job.samples.length : 0;
  },

  HypaAudio_CopyAndFree__deps: ["$HypaAudio"],
  HypaAudio_CopyAndFree: function (id, dstPtr, maxLen) {
    var job = HypaAudio.jobs[id];
    delete HypaAudio.jobs[id];
    if (!job || !job.samples) return 0;
    var take = Math.min(job.samples.length, maxLen);
    HEAPF32.set(job.samples.subarray(0, take), dstPtr >> 2);
    return take;
  },

  HypaAudio_Free__deps: ["$HypaAudio"],
  HypaAudio_Free: function (id) {
    delete HypaAudio.jobs[id];
  },
});
