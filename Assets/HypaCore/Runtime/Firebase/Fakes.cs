using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    // In-memory stand-ins (docs/contracts.md, section 1). The editor and the tests run on these, so a feature works
    // without Firebase. They never touch the network and never hold a real token.

    public sealed class FakeAuth : IHypaAuth
    {
        public string Uid { get; private set; }
        public event Action<string> UidChanged;

        public Task<string> SignInAnonymouslyAsync()
        {
            if (Uid == null) { Uid = "fake-uid"; UidChanged?.Invoke(Uid); }
            return Task.FromResult(Uid);
        }

        public Task LinkGoogleAsync() => Link();
        public Task LinkAppleAsync() => Link();
        public Task<string> IdTokenAsync() => Uid == null ? throw new InvalidOperationException("Not signed in.") : Task.FromResult("fake-id-token");
        public Task<string> AppCheckTokenAsync() => Task.FromResult("fake-app-check-token");

        Task Link() => Uid == null ? throw new InvalidOperationException("Not signed in.") : Task.CompletedTask;
    }

    public sealed class FakeProgressStore : IProgressStore
    {
        Progress _saved;
        public event Action<Progress> ChangedElsewhere;

        public Task<Progress> LoadAsync() => Task.FromResult(_saved);
        public Task SaveAsync(Progress p) { _saved = p; return Task.CompletedTask; }
        /// <summary>For tests: pretend another device saved this progress.</summary>
        public void SimulateChangeElsewhere(Progress p) { _saved = p; ChangedElsewhere?.Invoke(p); }
    }

    public sealed class FakeRemoteFlags : IRemoteFlags
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public FakeRemoteFlags Set(string key, string value) { _values[key] = value; return this; }
        public Task FetchAsync() => Task.CompletedTask;
        public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : (RemoteConfigKeys.Default(key) ?? fallback);
    }

    public sealed class FakeRoomSync : IRoomSync { }

    public sealed class FakeOnDeviceModel : IOnDeviceModel
    {
        public bool Available { get; set; }
        public string Reply = "This is a fake on-device answer.";

        public Task<string> StatusAsync() => Task.FromResult(Available ? "available" : "unavailable");
        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
            => Available ? Task.FromResult(Reply) : throw new InvalidOperationException("No on-device model.");
        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => Words(Reply, Available, ct);

        internal static async IAsyncEnumerable<string> Words(string reply, bool available, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!available) throw new InvalidOperationException("No on-device model.");
            foreach (var word in reply.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return word + " ";
                // Never suspends. A real Yield would resume on Unity's main thread, which deadlocks a test that waits on it.
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    public sealed class FakeCloudModel : ICloudModel
    {
        public string Reply = "This is a fake cloud answer.";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct) => Task.FromResult(Reply);
        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => FakeOnDeviceModel.Words(Reply, true, ct);
    }
}
