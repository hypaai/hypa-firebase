using System;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HypaCore.Tests
{
    public class HybridUpgradeTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        [TearDown] public void Reset() => HybridServices.Override(null);

        [Test] public void Upgrade_ReplacesTheFakes_AndRaisesUpgraded()
        {
            var local = new HybridServices();
            HybridServices.Override(local);
            var realAuth = new FakeAuth();
            int raised = 0; Action onUpgraded = () => raised++;
            HybridServices.Upgraded += onUpgraded;
            try
            {
                Run(HybridServices.UpgradeAsync(() => Task.FromResult(new HybridServices { Auth = realAuth })));
            }
            finally { HybridServices.Upgraded -= onUpgraded; }

            Assert.AreSame(realAuth, HybridServices.Current.Auth);
            Assert.AreEqual(1, raised);
        }

        [Test] public void Upgrade_SendsProgressSavedMeanwhile_IfItIsNewer()
        {
            var local = new HybridServices();
            HybridServices.Override(local);
            Run(local.Progress.SaveAsync(new Progress { storyId = "s1", lineIndex = 8, updatedAt = 200 }));

            var realStore = new FakeProgressStore();
            Run(realStore.SaveAsync(new Progress { storyId = "s1", lineIndex = 2, updatedAt = 100 }));
            Run(HybridServices.UpgradeAsync(() => Task.FromResult(new HybridServices { Progress = realStore })));

            Assert.AreEqual(8, Run(realStore.LoadAsync()).lineIndex, "the newer local progress was sent");
        }

        [Test] public void Upgrade_KeepsTheServersProgress_IfItIsNewer()
        {
            var local = new HybridServices();
            HybridServices.Override(local);
            Run(local.Progress.SaveAsync(new Progress { storyId = "s1", lineIndex = 8, updatedAt = 100 }));

            var realStore = new FakeProgressStore();
            Run(realStore.SaveAsync(new Progress { storyId = "s1", lineIndex = 20, updatedAt = 300 }));
            Run(HybridServices.UpgradeAsync(() => Task.FromResult(new HybridServices { Progress = realStore })));

            Assert.AreEqual(20, Run(realStore.LoadAsync()).lineIndex);
        }

        [Test] public void Upgrade_WhenStartingFails_KeepsTheFakes_AndDoesNotThrow()
        {
            var local = new HybridServices();
            HybridServices.Override(local);
            var fakeAuth = local.Auth;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Firebase did not start \\(InvalidOperationException\\)"));

            Assert.DoesNotThrow(() => Run(HybridServices.UpgradeAsync(() => throw new InvalidOperationException("secret detail"))));

            Assert.AreSame(fakeAuth, HybridServices.Current.Auth);
        }

        [Test] public void Upgrade_WithNothingToStart_ChangesNothing()
        {
            var local = new HybridServices();
            HybridServices.Override(local);
            var fakeAuth = local.Auth;

            Run(HybridServices.UpgradeAsync(() => Task.FromResult<HybridServices>(null)));

            Assert.AreSame(fakeAuth, HybridServices.Current.Auth);
        }
    }
}
