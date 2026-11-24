using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// The on-device model for platforms that have none (Quest, the editor): never available, so the router skips it.
    /// Asking it to generate is a mistake in the caller, so it throws.
    /// </summary>
    public sealed class NoOnDeviceModel : IOnDeviceModel
    {
        public bool Available => false;

        public Task<string> StatusAsync() => Task.FromResult("unavailable");

        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
            => throw new InvalidOperationException("There is no on-device model on this platform.");

        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct)
            => throw new InvalidOperationException("There is no on-device model on this platform.");
    }
}
