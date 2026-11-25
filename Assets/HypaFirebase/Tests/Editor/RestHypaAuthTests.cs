using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaFirebase.Rest;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestHypaAuthTests
    {
        const string SignUpReply = "{\"idToken\":\"id-1\",\"refreshToken\":\"refresh-1\",\"expiresIn\":\"3600\",\"localId\":\"user-1\"}";

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        sealed class Memory : IRefreshTokenStore
        {
            public string Value;
            public string Load() => Value;
            public void Save(string refreshToken) => Value = refreshToken;
            public void Clear() => Value = null;
        }

        static RestHypaAuth NewAuth(StubHandler http)
            => new RestHypaAuth(new RestAuthSession("KEY", new Memory(), http), new RestAppCheck("KEY", "proj", "app", "debug-xyz", http));

        [Test] public void SignIn_ReturnsTheUid_AndTellsListeners()
        {
            var http = new StubHandler().Reply(SignUpReply);
            var auth = NewAuth(http);
            var seen = new List<string>(); auth.UidChanged += seen.Add;

            var uid = Run(auth.SignInAnonymouslyAsync());

            Assert.AreEqual("user-1", uid);
            Assert.AreEqual("user-1", auth.Uid);
            CollectionAssert.AreEqual(new[] { "user-1" }, seen);
        }

        [Test] public void Tokens_ComeFromTheSessionAndAppCheck()
        {
            var http = new StubHandler().Reply(SignUpReply).Reply("{\"token\":\"ac-1\",\"ttl\":\"3600s\"}");
            var auth = NewAuth(http);
            Run(auth.SignInAnonymouslyAsync());

            Assert.AreEqual("id-1", Run(auth.IdTokenAsync()));
            Assert.AreEqual("ac-1", Run(auth.AppCheckTokenAsync()));
        }

        [Test] public void Linking_IsNotAvailableOnQuest()
        {
            var auth = NewAuth(new StubHandler());
            Assert.Throws<NotSupportedException>(() => Run(auth.LinkGoogleAsync()));
            Assert.Throws<NotSupportedException>(() => Run(auth.LinkAppleAsync()));
        }
    }
}
