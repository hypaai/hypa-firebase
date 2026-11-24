using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// The platform services every feature goes through (docs/contracts.md, section 1). Stories and Lens use these interfaces and
// never reference Firebase types. Native (Android, iOS, Quest), Web (WebGL) and Fake (editor, tests) implement them.
// The namespace is not HypaCore.Firebase on purpose: that name would hide the Firebase SDK's own "Firebase" namespace.
namespace HypaCore.Hybrid
{
    public interface IHypaAuth
    {
        /// <summary>Signs in anonymously and returns the uid. Returns the same uid on later launches.</summary>
        Task<string> SignInAnonymouslyAsync();
        /// <summary>Links Google to the signed-in user (Android, iOS). Progress is kept.</summary>
        Task LinkGoogleAsync();
        /// <summary>Links Apple to the signed-in user (iOS). Progress is kept.</summary>
        Task LinkAppleAsync();
        /// <summary>The signed-in user's uid, or null.</summary>
        string Uid { get; }
        /// <summary>Raised with the new uid (null after sign-out).</summary>
        event Action<string> UidChanged;
        /// <summary>A Firebase ID token, fetched on demand. Never persisted by gameplay, never logged.</summary>
        Task<string> IdTokenAsync();
        /// <summary>An App Check token, fetched on demand. Never persisted, never logged.</summary>
        Task<string> AppCheckTokenAsync();
    }

    public interface IProgressStore
    {
        /// <summary>The saved progress, or null if the player has none yet.</summary>
        Task<Progress> LoadAsync();
        /// <summary>Saves progress. Completes once it is stored locally; it reaches the server when the network allows.</summary>
        Task SaveAsync(Progress p);
        /// <summary>Raised when progress changed on another device.</summary>
        event Action<Progress> ChangedElsewhere;
    }

    public interface IRemoteFlags
    {
        /// <summary>Fetches and activates the latest values. Safe to fail offline.</summary>
        Task FetchAsync();
        /// <summary>A value (see <see cref="RemoteConfigKeys"/>), or the fallback until one has been fetched.</summary>
        string Get(string key, string fallback);
    }

    /// <summary>G09 owns this: create or join a room, presence, the story clock and the pose stream. Left empty until then.</summary>
    public interface IRoomSync { }

    public interface IOnDeviceModel
    {
        bool Available { get; }
        /// <summary>"available", "downloadable", "downloading" or "unavailable".</summary>
        Task<string> StatusAsync();
        Task<string> GenerateAsync(string prompt, CancellationToken ct);
        IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct);
    }

    public interface ICloudModel
    {
        Task<string> GenerateAsync(string prompt, CancellationToken ct);
        IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct);
    }
}
