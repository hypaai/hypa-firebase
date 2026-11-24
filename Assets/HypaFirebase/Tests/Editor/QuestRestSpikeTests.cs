using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HypaFirebase.Rest;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class QuestRestSpikeTests
    {
        const string SignUpReply = "{\"idToken\":\"secret-id-token\",\"refreshToken\":\"secret-refresh\",\"expiresIn\":\"3600\",\"localId\":\"user-123456789\"}";
        const string AppCheckReply = "{\"token\":\"secret-app-check\",\"ttl\":\"3600s\"}";
        const string ProgressDoc = "{\"fields\":{\"storyId\":{\"stringValue\":\"spike\"},\"lineIndex\":{\"integerValue\":\"42\"},\"updatedAt\":{\"integerValue\":\"1\"}}}";

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static QuestRestSpikeRunner Runner(StubHandler http, out MemoryKeyValueStore store)
        {
            store = new MemoryKeyValueStore();
            return new QuestRestSpikeRunner(new FirebaseRestConfig("KEY", "proj", "app", "secret-debug-token", "https://db.example"), store, http, () => 42);
        }

        [Test] public void EveryStepPasses_WhenTheRestPathWorks_AndTheReportHoldsNoToken()
        {
            var http = new StubHandler()
                .Reply(SignUpReply).Reply(AppCheckReply)                  // sign-in, App Check
                .Reply("{}").Reply(ProgressDoc)                            // progress: save, load
                .Reply("{}").Reply("{\"host\":\"user-123456789\"}")        // room: write, read
                .Reply("event: put\ndata: {\"path\":\"/\",\"data\":null}\n\n") // event stream
                .Reply("null");                                            // clean up
            var runner = Runner(http, out _);

            var steps = Run(runner.RunAsync(CancellationToken.None));
            var report = QuestRestSpikeRunner.Format(steps);

            Assert.IsTrue(steps.All(s => s.Passed), report);
            Assert.AreEqual(7, steps.Count);
            StringAssert.Contains("RESULT: the REST path works on this device.", report);
            foreach (var secret in new[] { "secret-id-token", "secret-refresh", "secret-app-check", "secret-debug-token", "user-123456789" })
                StringAssert.DoesNotContain(secret, report);
        }

        [Test] public void WhenSignInFails_ItStopsThere_AndSaysWhy()
        {
            var http = new StubHandler().Reply("{\"error\":{\"message\":\"ADMIN_ONLY_OPERATION\"}}", HttpStatusCode.BadRequest);
            var runner = Runner(http, out _);

            var steps = Run(runner.RunAsync(CancellationToken.None));
            var report = QuestRestSpikeRunner.Format(steps);

            Assert.AreEqual(1, steps.Count);
            Assert.IsFalse(steps[0].Passed);
            StringAssert.Contains("400 ADMIN_ONLY_OPERATION", report);
            StringAssert.Contains("RESULT: 1 step(s) failed.", report);
        }

        [Test] public void WhenTheDatabaseRefusesTheWrite_ThatStepFails_AndTheStreamAndCleanUpAreSkipped()
        {
            var http = new StubHandler()
                .Reply(SignUpReply).Reply(AppCheckReply)
                .Reply("{}").Reply(ProgressDoc)
                .Reply("{\"error\":\"Permission denied\"}", HttpStatusCode.Forbidden); // the room write is refused
            var runner = Runner(http, out _);

            var steps = Run(runner.RunAsync(CancellationToken.None));

            Assert.AreEqual("Realtime Database write and read", steps.Last().Name);
            Assert.IsFalse(steps.Last().Passed);
            StringAssert.Contains("403", steps.Last().Detail);
            Assert.AreEqual(5, steps.Count, "no stream or clean-up when there is no room");
            Assert.IsTrue(steps.Take(4).All(s => s.Passed));
        }
    }
}
