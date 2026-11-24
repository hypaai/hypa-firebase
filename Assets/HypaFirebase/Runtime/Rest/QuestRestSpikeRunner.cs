using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    public sealed class SpikeStep
    {
        public string Name { get; }
        public bool Passed { get; }
        public string Detail { get; }
        public SpikeStep(string name, bool passed, string detail) { Name = name; Passed = passed; Detail = detail; }
    }

    /// <summary>
    /// The Quest spike for the REST path (HYP-197, scope item 6): runs each REST call the game would need, one step at a time,
    /// and reports which worked. Put <see cref="QuestRestSpike"/> in a scene, run it on a Quest, and read the report in the log.
    /// The report never contains a token: a step says only that a token arrived, how long it is, or which error code came back.
    /// It writes only to <c>rooms/spike-{uid}</c> (its own room, so a run that failed before clean-up never blocks someone
    /// else's) and the signed-in user's own progress document, and removes the room afterwards.
    /// </summary>
    public sealed class QuestRestSpikeRunner
    {
        readonly FirebaseRestConfig _config;
        readonly IKeyValueStore _store;
        readonly HttpMessageHandler _handler;
        readonly Func<int> _nonce;

        public QuestRestSpikeRunner(FirebaseRestConfig config, IKeyValueStore store, HttpMessageHandler handler = null, Func<int> nonce = null)
        {
            _config = config;
            _store = store;
            _handler = handler;
            _nonce = nonce ?? (() => new Random().Next(1, 10000));
        }

        public async Task<IReadOnlyList<SpikeStep>> RunAsync(CancellationToken ct)
        {
            var steps = new List<SpikeStep>();
            var session = new RestAuthSession(_config.ApiKey, new PlayerPrefsRefreshTokenStore(_store), _handler);
            var appCheck = new RestAppCheck(_config.ApiKey, _config.ProjectId, _config.AppId, _config.AppCheckDebugToken, _handler);
            var auth = new RestHypaAuth(session, appCheck);

            if (!await Step(steps, "Anonymous sign-in", async () =>
            {
                var uid = await auth.SignInAnonymouslyAsync();
                return $"signed in as {uid.Substring(0, Math.Min(6, uid.Length))}...";
            })) return steps;

            await Step(steps, "ID token", async () => $"received, {(await auth.IdTokenAsync()).Length} characters");
            await Step(steps, "App Check token", async () => { await auth.AppCheckTokenAsync(); return "received"; });

            await Step(steps, "Progress save and load (Firestore)", async () =>
            {
                var progress = new RestProgressStore(_config, auth, _store, _handler);
                var line = _nonce();
                await progress.SaveAsync(new Progress { storyId = "spike", lineId = "spike", lineIndex = line, language = "en", place = "camp", updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), device = "quest-spike" });
                if (_store.Get(RestProgressStore.DirtyKey) != null) throw new InvalidOperationException("saved on the device, but Firestore did not take it");
                var back = await progress.LoadAsync();
                if (back == null || back.lineIndex != line) throw new InvalidOperationException("read back something different from what was saved");
                return "saved and read back from the server";
            });

            var db = new RestRealtimeDatabase(_config, auth, _handler);
            var roomPath = "rooms/spike-" + auth.Uid;
            if (await Step(steps, "Realtime Database write and read", async () =>
            {
                var uid = auth.Uid;
                var room = new JObject { ["host"] = uid, ["storyId"] = "spike", ["language"] = "en", ["state"] = "lobby", ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                await db.SetAsync(roomPath, room.ToString(Newtonsoft.Json.Formatting.None), ct);
                var read = JObject.Parse(await db.GetAsync(roomPath, ct));
                if ((string)read["host"] != uid) throw new InvalidOperationException("read back something different from what was written");
                return "wrote a room and read it back";
            }))
            {
                await Step(steps, "Realtime Database event stream", async () =>
                {
                    using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        limit.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            await foreach (var evt in db.StreamAsync(roomPath, limit.Token)) return $"first event: {evt.Name}";
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("no event within 10 seconds"); }
                    }
                    throw new InvalidOperationException("the stream ended without an event");
                });
                await Step(steps, "Clean up the test room", async () => { await db.DeleteAsync(roomPath, ct); return "removed"; });
            }

            return steps;
        }

        /// <summary>The report for the log: one line per step, then the result.</summary>
        public static string Format(IReadOnlyList<SpikeStep> steps)
        {
            var text = new StringBuilder("QUEST REST SPIKE\n");
            var failed = 0;
            foreach (var step in steps)
            {
                text.Append(step.Passed ? "[PASS] " : "[FAIL] ").Append(step.Name).Append(": ").Append(step.Detail).Append('\n');
                if (!step.Passed) failed++;
            }
            text.Append(failed == 0 ? "RESULT: the REST path works on this device." : $"RESULT: {failed} step(s) failed.");
            return text.ToString();
        }

        static async Task<bool> Step(List<SpikeStep> steps, string name, Func<Task<string>> body)
        {
            try
            {
                steps.Add(new SpikeStep(name, true, await body()));
                return true;
            }
            catch (Exception e)
            {
                // The error code Firebase sent, or just the kind of failure. Never the message of a network error, which could name a URL.
                var detail = e is HypaFirebaseException firebase ? $"{firebase.StatusCode} {firebase.Message}"
                    : e is InvalidOperationException || e is TimeoutException ? e.Message
                    : e.GetType().Name;
                steps.Add(new SpikeStep(name, false, detail));
                return false;
            }
        }
    }
}
