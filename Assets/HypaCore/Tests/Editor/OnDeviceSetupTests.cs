using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaCore.Tests
{
    /// <summary>The settings entry and the route reason for the on-device model: the state, the words, the progress, retry.</summary>
    public class OnDeviceSetupTests
    {
        static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
        static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

        sealed class FakeDownloadable : IDownloadableOnDeviceModel
        {
            public string Status = "downloadable";
            public int Downloads;
            public Func<IProgress<float>, CancellationToken, Task> OnDownload = (progress, ct) => Task.CompletedTask;

            public bool Available => Status == "available";
            public Task<string> StatusAsync() => Task.FromResult(Status);
            public Task<string> GenerateAsync(string prompt, CancellationToken ct) => throw new NotSupportedException();
            public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => throw new NotSupportedException();

            public async Task DownloadAsync(IProgress<float> progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Downloads);
                await OnDownload(progress, ct).ConfigureAwait(false);
            }
        }

        [Test] public void NoModel_IsUnavailable_WithTheQuestWords()
        {
            var setup = new OnDeviceSetup(new NoOnDeviceModel());
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Unavailable, setup.State);
            Assert.AreEqual("This phone has no on-device model", setup.Label);
            Assert.AreEqual("this phone has no on-device model", setup.RouteReason);
            Assert.IsFalse(setup.CanDownload);
        }

        [Test] public void Downloadable_OffersTheDownload()
        {
            var setup = new OnDeviceSetup(new FakeDownloadable { Status = "downloadable" });
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Downloadable, setup.State);
            Assert.AreEqual("Get the on-device storyteller", setup.Label);
            Assert.AreEqual("the on-device model is not downloaded yet", setup.RouteReason);
            Assert.IsTrue(setup.CanDownload);
        }

        [Test] public void Available_IsReady_WithNoReasonToGiveTheRouteChip()
        {
            var setup = new OnDeviceSetup(new FakeDownloadable { Status = "available" });
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Ready, setup.State);
            Assert.AreEqual("On-device storyteller ready", setup.Label);
            Assert.AreEqual("", setup.RouteReason);
            Assert.IsFalse(setup.CanDownload);
        }

        [Test] public void AModelThatCannotBeDownloaded_NeverOffersIt()
        {
            var setup = new OnDeviceSetup(new FakeOnDeviceModel { Available = false });
            Run(() => setup.RefreshAsync());
            Assert.IsFalse(setup.CanDownload);
            Run(() => setup.DownloadAsync());
            Assert.AreEqual(OnDeviceSetupState.Unavailable, setup.State);
        }

        [Test] public void Download_ShowsProgress_ThenBecomesReady()
        {
            var model = new FakeDownloadable();
            var seen = new List<string>();
            model.OnDownload = (progress, ct) =>
            {
                progress.Report(0f);
                progress.Report(0.4f);
                progress.Report(1f);
                model.Status = "available";
                return Task.CompletedTask;
            };
            var setup = new OnDeviceSetup(model);
            setup.Changed += () => { lock (seen) seen.Add(setup.Label); };
            Run(() => setup.RefreshAsync());

            Run(() => setup.DownloadAsync());

            Assert.AreEqual(OnDeviceSetupState.Ready, setup.State);
            CollectionAssert.Contains(seen, "Getting the on-device storyteller… 40%");
            Assert.AreEqual("On-device storyteller ready", setup.Label);
            Assert.IsNull(setup.Problem);
        }

        [Test] public void Download_WhileDownloading_IsIgnored()
        {
            var model = new FakeDownloadable();
            var release = new TaskCompletionSource<bool>();
            model.OnDownload = async (progress, ct) => { await release.Task.ConfigureAwait(false); model.Status = "available"; };
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());

            var first = Task.Run(() => setup.DownloadAsync());
            SpinWait.SpinUntil(() => setup.State == OnDeviceSetupState.Downloading, 2000);
            Assert.IsFalse(setup.CanDownload);
            Run(() => setup.DownloadAsync());
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Downloading, setup.State, "A refresh must not overwrite a download in progress.");

            release.SetResult(true);
            first.GetAwaiter().GetResult();
            Assert.AreEqual(1, model.Downloads);
            Assert.AreEqual(OnDeviceSetupState.Ready, setup.State);
        }

        [TestCase("no-space", "There is not enough space on this phone.")]
        [TestCase("needs-update", "This phone needs a system update first.")]
        [TestCase("busy", "The phone is busy right now.")]
        [TestCase("error", "The download did not finish.")]
        public void Download_WhenItFails_SaysWhy_AndCanBeRetried(string reason, string words)
        {
            var model = new FakeDownloadable { OnDownload = (progress, ct) => throw new OnDeviceModelException(reason) };
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());

            Assert.DoesNotThrow(() => Run(() => setup.DownloadAsync()));

            Assert.AreEqual(OnDeviceSetupState.Failed, setup.State);
            Assert.AreEqual(words, setup.Problem);
            Assert.AreEqual(words + " Tap to try again.", setup.Label);
            Assert.AreEqual("the on-device model could not be downloaded", setup.RouteReason);
            Assert.IsTrue(setup.CanDownload);
        }

        [Test] public void Download_RetryAfterAFailure_Works_AndClearsTheProblem()
        {
            var attempt = 0;
            var model = new FakeDownloadable();
            model.OnDownload = (progress, ct) =>
            {
                if (++attempt == 1) throw new OnDeviceModelException("no-space");
                model.Status = "available";
                return Task.CompletedTask;
            };
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());
            Run(() => setup.DownloadAsync());
            Assert.AreEqual(OnDeviceSetupState.Failed, setup.State);

            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Failed, setup.State, "A refresh does not hide a failure the player has not seen.");

            Run(() => setup.DownloadAsync());
            Assert.AreEqual(OnDeviceSetupState.Ready, setup.State);
            Assert.IsNull(setup.Problem);
        }

        [Test] public void Download_AnUnexpectedError_IsAFailure_NotACrash()
        {
            var model = new FakeDownloadable { OnDownload = (progress, ct) => throw new InvalidOperationException("boom") };
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());
            Assert.DoesNotThrow(() => Run(() => setup.DownloadAsync()));
            Assert.AreEqual(OnDeviceSetupState.Failed, setup.State);
        }

        [Test] public void Download_Cancelled_GoesBackToDownloadable()
        {
            var model = new FakeDownloadable { OnDownload = (progress, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Run(() => setup.DownloadAsync(cts.Token));
            }
            Assert.AreEqual(OnDeviceSetupState.Downloadable, setup.State);
            Assert.IsTrue(setup.CanDownload);
        }

        [Test] public void Progress_StaysBetweenZeroAndOne()
        {
            var model = new FakeDownloadable();
            model.OnDownload = (progress, ct) => { progress.Report(-3f); progress.Report(7f); model.Status = "available"; return Task.CompletedTask; };
            var setup = new OnDeviceSetup(model);
            var seen = new List<float>();
            setup.Changed += () => { lock (seen) seen.Add(setup.Progress); };
            Run(() => setup.RefreshAsync());
            Run(() => setup.DownloadAsync());
            foreach (var value in seen) Assert.That(value, Is.InRange(0f, 1f));
        }

        [Test] public void RefreshRaisesChanged_AndTheModelBecomingAvailableIsPickedUp()
        {
            var model = new FakeDownloadable { Status = "downloading" };
            var setup = new OnDeviceSetup(model);
            var raised = 0;
            setup.Changed += () => raised++;
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Downloading, setup.State);
            model.Status = "available";
            Run(() => setup.RefreshAsync());
            Assert.AreEqual(OnDeviceSetupState.Ready, setup.State);
            Assert.AreEqual(2, raised);
        }

        [Test] public void ShortLabel_FitsAButton_InEveryState()
        {
            var model = new FakeDownloadable();
            var setup = new OnDeviceSetup(model);
            Run(() => setup.RefreshAsync());
            Assert.AreEqual("Get on-device AI (one-time download)", setup.ShortLabel);

            model.OnDownload = (progress, ct) => { throw new OnDeviceModelException("no-space"); };
            Run(() => setup.DownloadAsync());
            Assert.AreEqual("On-device AI: download failed, tap to retry", setup.ShortLabel);

            var ready = new OnDeviceSetup(new FakeDownloadable { Status = "available" });
            Run(() => ready.RefreshAsync());
            Assert.AreEqual("On-device AI: ready", ready.ShortLabel);

            var none = new OnDeviceSetup(new NoOnDeviceModel());
            Run(() => none.RefreshAsync());
            Assert.AreEqual("On-device AI: cloud on this phone", none.ShortLabel);

            foreach (var label in new[] { setup.ShortLabel, ready.ShortLabel, none.ShortLabel }) Assert.LessOrEqual(label.Length, 46);
        }

        [Test] public void NullModel_IsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new OnDeviceSetup(null));
        }
    }
}
