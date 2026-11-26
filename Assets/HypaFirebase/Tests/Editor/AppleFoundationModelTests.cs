using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Native;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class AppleFoundationModelTests
    {
        /// <summary>Stands in for the Swift plugin: a fixed status, and replies sent as soon as a request starts.</summary>
        sealed class FakeSwift : AppleFoundationModel.INative
        {
            public int StatusCode;
            public readonly List<(int kind, string text)> Replies = new List<(int, string)>();
            public readonly List<(int id, string prompt, bool stream)> Started = new List<(int, string, bool)>();
            public readonly List<int> Cancelled = new List<int>();
            public Exception StartFails;

            public int Status() => StatusCode;

            public void Start(int id, string prompt, bool stream)
            {
                if (StartFails != null) throw StartFails;
                Started.Add((id, prompt, stream));
                foreach (var (kind, text) in Replies) AppleFoundationModel.Receive(id, kind, text);
            }

            public void Cancel(int id) => Cancelled.Add(id);
        }

        FakeSwift _swift;
        SynchronizationContext _context;

        [SetUp]
        public void SetUp()
        {
            // The editor's context runs continuations on later frames; these tests wait on tasks directly.
            _context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _swift = new FakeSwift();
            AppleFoundationModel.Native = _swift;
        }

        [TearDown]
        public void TearDown()
        {
            AppleFoundationModel.ResetForTests();
            HybridServices.OnDeviceFactory = null;
            SynchronizationContext.SetSynchronizationContext(_context);
        }

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static List<string> Collect(IAsyncEnumerable<string> stream, CancellationToken ct = default)
        {
            return Run(Read());
            async Task<List<string>> Read()
            {
                var all = new List<string>();
                await foreach (var chunk in stream.WithCancellation(ct)) all.Add(chunk);
                return all;
            }
        }

        [TestCase(0, "available", true)]
        [TestCase(1, "downloading", false)]
        [TestCase(2, "unavailable", false)]
        [TestCase(3, "unavailable", false)]
        [TestCase(4, "unavailable", false)]
        public void Status_FollowsApplesAvailability(int code, string status, bool available)
        {
            _swift.StatusCode = code;
            var model = new AppleFoundationModel();
            Assert.AreEqual(status, Run(model.StatusAsync()));
            Assert.AreEqual(available, model.Available);
            Assert.AreEqual(code >= 2, AppleFoundationModel.UnavailableReason != null);
        }

        [Test]
        public void Generate_ReturnsTheAnswer()
        {
            _swift.Replies.Add((1, "Once upon a time"));
            Assert.AreEqual("Once upon a time", Run(new AppleFoundationModel().GenerateAsync("Tell me a story", default)));
            Assert.AreEqual(("Tell me a story", false), (_swift.Started[0].prompt, _swift.Started[0].stream));
        }

        [TestCase("unavailable")]
        [TestCase("too_long")]
        [TestCase("guardrail")]
        public void Generate_FailsWithTheModelsCode(string code)
        {
            _swift.Replies.Add((2, code));
            var e = Assert.Throws<OnDeviceModelException>(() => Run(new AppleFoundationModel().GenerateAsync("Hi", default)));
            Assert.AreEqual(code, e.Reason);
        }

        [Test]
        public void Generate_FailsWhenThePluginCannotStart()
        {
            _swift.StartFails = new PlatformNotSupportedException();
            Assert.Throws<PlatformNotSupportedException>(() => Run(new AppleFoundationModel().GenerateAsync("Hi", default)));
        }

        [Test]
        public void Generate_Cancelled_StopsTheModel_AndDropsTheLateAnswer()
        {
            using var cts = new CancellationTokenSource();
            var task = new AppleFoundationModel().GenerateAsync("Hi", cts.Token);
            cts.Cancel();
            AppleFoundationModel.Receive(_swift.Started[0].id, 1, "too late");
            Assert.That(() => Run(task), Throws.InstanceOf<OperationCanceledException>());
            Assert.AreEqual(new[] { _swift.Started[0].id }, _swift.Cancelled);
        }

        [Test]
        public void Stream_YieldsEachChunk_ThenEnds()
        {
            _swift.Replies.AddRange(new[] { (0, "Once "), (0, "upon "), (0, "a time"), (1, "") });
            Assert.AreEqual(new[] { "Once ", "upon ", "a time" }, Collect(new AppleFoundationModel().StreamAsync("Story", default)));
            Assert.IsTrue(_swift.Started[0].stream);
            Assert.IsEmpty(_swift.Cancelled);
        }

        [Test]
        public void Stream_ThrowsTheModelsError_AfterTheChunksBeforeIt()
        {
            _swift.Replies.AddRange(new[] { (0, "Once "), (2, "guardrail") });
            var seen = new List<string>();
            var e = Assert.Throws<OnDeviceModelException>(() => Run(Read()));
            Assert.AreEqual("guardrail", e.Reason);
            Assert.AreEqual(new[] { "Once " }, seen);

            async Task<bool> Read()
            {
                await foreach (var chunk in new AppleFoundationModel().StreamAsync("Story", default)) seen.Add(chunk);
                return true;
            }
        }

        [Test]
        public void Stream_StoppedEarly_StopsTheModel()
        {
            _swift.Replies.AddRange(new[] { (0, "Once "), (0, "upon ") });
            Run(FirstOnly());
            Assert.AreEqual(new[] { _swift.Started[0].id }, _swift.Cancelled);
            AppleFoundationModel.Receive(_swift.Started[0].id, 0, "a time"); // late: dropped, nothing throws

            async Task<bool> FirstOnly()
            {
                await foreach (var _ in new AppleFoundationModel().StreamAsync("Story", default)) break;
                return true;
            }
        }

        [Test]
        public void Stream_Cancelled_WhileWaiting_StopsTheModel()
        {
            using var cts = new CancellationTokenSource();
            var e = new AppleFoundationModel().StreamAsync("Story", cts.Token).GetAsyncEnumerator(cts.Token);
            var next = e.MoveNextAsync().AsTask();
            cts.Cancel();
            Assert.That(() => Run(next), Throws.InstanceOf<OperationCanceledException>());
            Assert.AreEqual(new[] { _swift.Started[0].id }, _swift.Cancelled);
        }

        [Test]
        public void HybridServices_UseThePlatformsOnDeviceModel()
        {
            Assert.IsInstanceOf<NoOnDeviceModel>(new HybridServices().OnDevice);
            HybridServices.OnDeviceFactory = () => new AppleFoundationModel();
            Assert.IsInstanceOf<AppleFoundationModel>(new HybridServices().OnDevice);
        }
    }
}
