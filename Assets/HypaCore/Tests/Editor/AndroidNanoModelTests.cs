using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaCore.Tests
{
    /// <summary>The Android on-device model against a scripted bridge, so it runs anywhere. The real bridge needs a phone.</summary>
    public class AndroidNanoModelTests
    {
        // Off the test thread: in the editor a continuation would be sent back to Unity's main thread, which is blocked here.
        static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
        static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

        static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
        {
            var all = new List<string>();
            await foreach (var chunk in stream.ConfigureAwait(false)) all.Add(chunk);
            return all;
        }

        sealed class FakeBridge : INanoBridge
        {
            public string StatusValue = "available";
            public bool StatusThrows;
            public Action<INanoTextSink> OnGenerate = sink => sink.Done();
            public Action<INanoDownloadSink> OnDownload = sink => sink.Done();
            public int GenerateCalls;
            public int CancelCalls;
            public readonly List<string> Prompts = new List<string>();

            public string Status()
            {
                if (StatusThrows) throw new InvalidOperationException("no AICore");
                return StatusValue;
            }

            public void Generate(string prompt, int maxTokens, float temperature, INanoTextSink sink)
            {
                Interlocked.Increment(ref GenerateCalls);
                Prompts.Add(prompt);
                OnGenerate(sink);
            }

            public void Download(INanoDownloadSink sink) => OnDownload(sink);

            public void Cancel() => Interlocked.Increment(ref CancelCalls);
        }

        sealed class Reports : IProgress<float>
        {
            public readonly List<float> Values = new List<float>();
            public void Report(float value) { lock (Values) Values.Add(value); }
        }

        static AndroidNanoModel Ready(FakeBridge bridge)
        {
            var model = new AndroidNanoModel(bridge);
            Run(() => model.StatusAsync());
            return model;
        }

        [TearDown] public void Reset()
        {
            HybridServices.OnDeviceFactory = null;
            HybridServices.Override(null);
        }

        [Test] public void Status_Available_MakesTheModelAvailable()
        {
            var model = new AndroidNanoModel(new FakeBridge { StatusValue = "available" });
            Assert.IsFalse(model.Available, "Nothing is known until the status has been read.");
            Assert.AreEqual("available", Run(() => model.StatusAsync()));
            Assert.IsTrue(model.Available);
        }

        [TestCase("downloadable")]
        [TestCase("downloading")]
        public void Status_NotReady_IsNotAvailable_ButIsReported(string status)
        {
            var model = new AndroidNanoModel(new FakeBridge { StatusValue = status });
            Assert.AreEqual(status, Run(() => model.StatusAsync()));
            Assert.IsFalse(model.Available);
        }

        [TestCase("unavailable")]
        [TestCase("something new")]
        [TestCase(null)]
        public void Status_AnythingElse_IsUnavailable(string status)
        {
            var model = new AndroidNanoModel(new FakeBridge { StatusValue = status });
            Assert.AreEqual("unavailable", Run(() => model.StatusAsync()));
            Assert.IsFalse(model.Available);
        }

        [Test] public void Status_WhenTheBridgeThrows_IsUnavailable_AndDoesNotThrow()
        {
            var model = new AndroidNanoModel(new FakeBridge { StatusThrows = true });
            Assert.AreEqual("unavailable", Run(() => model.StatusAsync()));
            Assert.IsFalse(model.Available);
        }

        [Test] public void Status_CanChangeAfterTheModelIsDownloaded()
        {
            var bridge = new FakeBridge { StatusValue = "downloadable" };
            var model = new AndroidNanoModel(bridge);
            Run(() => model.StatusAsync());
            bridge.StatusValue = "available";
            Run(() => model.StatusAsync());
            Assert.IsTrue(model.Available);
        }

        [Test] public void Stream_DeliversTheChunksInOrder()
        {
            var model = Ready(new FakeBridge
            {
                OnGenerate = sink => { sink.Text("Hel"); sink.Text("lo, "); sink.Text("world"); sink.Done(); },
            });
            CollectionAssert.AreEqual(new[] { "Hel", "lo, ", "world" }, Run(() => Collect(model.StreamAsync("hi", CancellationToken.None))));
        }

        [Test] public void Stream_HandlesChunksArrivingFromAnotherThread()
        {
            var model = Ready(new FakeBridge
            {
                OnGenerate = sink => Task.Run(() =>
                {
                    for (var i = 0; i < 20; i++) { sink.Text(i.ToString() + ","); Thread.Sleep(1); }
                    sink.Done();
                }),
            });
            var chunks = Run(() => Collect(model.StreamAsync("count", CancellationToken.None)));
            var expected = new List<string>();
            for (var i = 0; i < 20; i++) expected.Add(i.ToString() + ",");
            CollectionAssert.AreEqual(expected, chunks);
        }

        [Test] public void Stream_WhenTheReplyEndsAtOnce_IsEmpty()
        {
            var model = Ready(new FakeBridge());
            Assert.IsEmpty(Run(() => Collect(model.StreamAsync("hi", CancellationToken.None))));
        }

        [Test] public void Generate_JoinsTheChunks_AndSendsThePromptOnce()
        {
            var bridge = new FakeBridge { OnGenerate = sink => { sink.Text("A tale "); sink.Text("of two."); sink.Done(); } };
            var model = Ready(bridge);
            Assert.AreEqual("A tale of two.", Run(() => model.GenerateAsync("Tell a story", CancellationToken.None)));
            CollectionAssert.AreEqual(new[] { "Tell a story" }, bridge.Prompts);
        }

        [TestCase("busy")]
        [TestCase("background")]
        [TestCase("no-space")]
        public void Stream_WhenTheModelFails_ThrowsWithTheReason_AndNotThePrompt(string reason)
        {
            var model = Ready(new FakeBridge { OnGenerate = sink => { sink.Text("Once"); sink.Error(reason); } });
            var thrown = Assert.Throws<OnDeviceModelException>(() => Run(() => Collect(model.StreamAsync("my secret prompt", CancellationToken.None))));
            Assert.AreEqual(reason, thrown.Reason);
            StringAssert.DoesNotContain("secret", thrown.Message);
        }

        [Test] public void Stream_AnEmptyReason_BecomesError()
        {
            var model = Ready(new FakeBridge { OnGenerate = sink => sink.Error("") });
            var thrown = Assert.Throws<OnDeviceModelException>(() => Run(() => Collect(model.StreamAsync("hi", CancellationToken.None))));
            Assert.AreEqual("error", thrown.Reason);
        }

        [Test] public void Stream_WhenTheBridgeCannotStart_Throws_NotHangs()
        {
            var model = Ready(new FakeBridge { OnGenerate = sink => sink.Error("unavailable") });
            var thrown = Assert.Throws<OnDeviceModelException>(() => Run(() => Collect(model.StreamAsync("hi", CancellationToken.None))));
            Assert.AreEqual("unavailable", thrown.Reason);
        }

        [Test] public void Stream_WhenTheModelIsNotReady_Throws_AndNeverCallsTheBridge()
        {
            var bridge = new FakeBridge { StatusValue = "downloadable" };
            var model = new AndroidNanoModel(bridge);
            Run(() => model.StatusAsync());
            Assert.Throws<InvalidOperationException>(() => Run(() => Collect(model.StreamAsync("hi", CancellationToken.None))));
            Assert.Throws<InvalidOperationException>(() => Run(() => model.GenerateAsync("hi", CancellationToken.None)));
            Assert.AreEqual(0, bridge.GenerateCalls);
        }

        [Test] public void Stream_Cancelling_StopsTheBridge_AndThrowsCancelled()
        {
            var bridge = new FakeBridge { OnGenerate = sink => sink.Text("first ") };
            var model = Ready(bridge);
            using (var cts = new CancellationTokenSource())
            {
                Assert.Catch<OperationCanceledException>(() => Run(async () =>
                {
                    await foreach (var chunk in model.StreamAsync("hi", cts.Token).WithCancellation(cts.Token).ConfigureAwait(false))
                    {
                        Assert.AreEqual("first ", chunk);
                        cts.Cancel();
                    }
                }));
            }
            Assert.AreEqual(1, bridge.CancelCalls);
        }

        [Test] public void Stream_AlreadyCancelled_NeverStarts()
        {
            var bridge = new FakeBridge();
            var model = Ready(bridge);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Catch<OperationCanceledException>(() => Run(() => Collect(model.StreamAsync("hi", cts.Token))));
            }
            Assert.AreEqual(0, bridge.GenerateCalls);
        }

        [Test] public void Download_ReportsProgress_AndTheModelBecomesAvailable()
        {
            var bridge = new FakeBridge { StatusValue = "downloadable" };
            bridge.OnDownload = sink =>
            {
                sink.Started(1000);
                sink.Progress(250);
                sink.Progress(1000);
                bridge.StatusValue = "available";
                sink.Done();
            };
            var model = new AndroidNanoModel(bridge);
            Run(() => model.StatusAsync());
            Assert.IsFalse(model.Available);

            var reports = new Reports();
            Run(() => model.DownloadAsync(reports, CancellationToken.None));

            Assert.IsTrue(model.Available);
            Assert.AreEqual(0f, reports.Values[0]);
            Assert.AreEqual(1f, reports.Values[reports.Values.Count - 1]);
            CollectionAssert.Contains(reports.Values, 0.25f);
            for (var i = 1; i < reports.Values.Count; i++) Assert.GreaterOrEqual(reports.Values[i], reports.Values[i - 1]);
        }

        [Test] public void Download_WithoutAReportTarget_StillWorks()
        {
            var bridge = new FakeBridge { StatusValue = "downloadable" };
            bridge.OnDownload = sink => { sink.Started(10); sink.Progress(5); bridge.StatusValue = "available"; sink.Done(); };
            var model = new AndroidNanoModel(bridge);
            Run(() => model.DownloadAsync(null, CancellationToken.None));
            Assert.IsTrue(model.Available);
        }

        [Test] public void Download_WhenItFails_ThrowsWithTheReason_AndTheModelStaysUnavailable()
        {
            var bridge = new FakeBridge { StatusValue = "downloadable", OnDownload = sink => sink.Error("no-space") };
            var model = new AndroidNanoModel(bridge);
            var thrown = Assert.Throws<OnDeviceModelException>(() => Run(() => model.DownloadAsync(null, CancellationToken.None)));
            Assert.AreEqual("no-space", thrown.Reason);
            Assert.IsFalse(model.Available);
        }

        [Test] public void Download_Cancelling_StopsWaiting()
        {
            var model = new AndroidNanoModel(new FakeBridge { StatusValue = "downloading", OnDownload = sink => sink.Started(100) });
            using (var cts = new CancellationTokenSource())
            {
                var waiting = Task.Run(() => model.DownloadAsync(null, cts.Token));
                Thread.Sleep(50);
                cts.Cancel();
                Assert.Catch<OperationCanceledException>(() => waiting.GetAwaiter().GetResult());
            }
        }

        [Test] public void HybridServices_UsesTheFactoryForNewServices_AndKeepsItWhenUpgraded()
        {
            var model = Ready(new FakeBridge());
            HybridServices.OnDeviceFactory = () => model;

            Assert.AreSame(model, new HybridServices().OnDevice);

            HybridServices.Override(new HybridServices());
            Run(() => HybridServices.UpgradeAsync(() => Task.FromResult(new HybridServices())));
            Assert.AreSame(model, HybridServices.Current.OnDevice);
        }

        [Test] public void HybridServices_WithoutAFactory_HasNoOnDeviceModel()
        {
            HybridServices.OnDeviceFactory = null;
            Assert.IsInstanceOf<NoOnDeviceModel>(new HybridServices().OnDevice);
            Assert.IsFalse(new HybridServices().OnDevice.Available);
        }

        [Test] public void NoOnDeviceModel_IsUnavailable_AsOnQuest()
        {
            var none = new NoOnDeviceModel();
            Assert.IsFalse(none.Available);
            Assert.AreEqual("unavailable", Run(() => none.StatusAsync()));
        }
    }
}
