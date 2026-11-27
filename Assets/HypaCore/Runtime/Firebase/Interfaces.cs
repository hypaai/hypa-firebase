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

    /// <summary>
    /// A shared story for two players (Realtime Database <c>rooms/{code}</c>, docs/contracts.md, section 4; G09 implements it).
    /// The host creates the room and owns its state and story clock; each player owns their own seat, pose and game entries.
    /// When the host goes (leaves, or is offline for <see cref="RoomPresence.TimeoutMs"/>), the next player takes the room
    /// (<see cref="RoomHandover.NextHost"/>) and the story carries on from the same clock. Events are raised on the main thread.
    /// One room at a time.
    /// </summary>
    public interface IRoomSync
    {
        /// <summary>The room this client is in, or null.</summary>
        string Code { get; }
        /// <summary>True when this client is the room's host: it created the room, or took it over when the host went.</summary>
        bool IsHost { get; }
        /// <summary>
        /// The server's clock in Unix ms, estimated from this device's clock. The SDKs read <c>.info/serverTimeOffset</c>; REST has no
        /// such node, so the REST implementation measures it (docs/contracts.md, section 4, "Server time").
        /// </summary>
        long ServerTimeMs { get; }

        /// <summary>Creates a room with a new <see cref="RoomCode"/>, seats you in seat 0, and returns the code.</summary>
        Task<string> CreateAsync(string storyId, string language, RoomPlayer me);
        /// <summary>Joins a room in the free seat. Throws <see cref="RoomException"/> (NotFound, Full, Expired).</summary>
        Task<RoomInfo> JoinAsync(string code, RoomPlayer me);
        /// <summary>
        /// Leaves the room. When the host leaves and another player is online, that player takes the room; it closes only when nobody
        /// is left. Raises <see cref="Closed"/> for the player who left.
        /// </summary>
        Task LeaveAsync();

        /// <summary>Host only: "lobby", "story" or "game" (<see cref="RoomStates"/>).</summary>
        Task SetStateAsync(string state);
        /// <summary>Host only: where the story is, so both players hear the same line at the same time.</summary>
        Task SetClockAsync(RoomClock clock);
        /// <summary>Updates your own entry (e.g. a raised hand). Presence (<c>online</c>) is kept by the implementation.</summary>
        Task UpdateMeAsync(RoomPlayer me);
        /// <summary>Sends your head and hands. Fire and forget; the implementation sends at most 10 per second and drops the rest.</summary>
        void SendPose(PoseFrame pose);
        /// <summary>
        /// Adds one sentence of the answer to your question (<c>answers/{n}</c>). Your hand must be up (<see cref="RoomPlayer.handUp"/>),
        /// or it throws <see cref="RoomException"/> <see cref="RoomError.NotYourTurn"/>. The implementation sets <see cref="RoomAnswer.uid"/>
        /// and picks <c>n</c>: one more than the highest it has seen in the room, including sentences from before it joined, and the
        /// next one up if the server refuses it because that <c>n</c> is taken. Await the last sentence before lowering your hand: the
        /// rules refuse a sentence from a player whose hand is down.
        /// </summary>
        Task SendAnswerAsync(RoomAnswer sentence);
        /// <summary>Records your game result.</summary>
        Task SubmitGameAsync(GameResult result);

        event Action<RoomInfo> RoomChanged;
        event Action<RoomClock> ClockChanged;
        /// <summary>Everyone in the room by uid, you included.</summary>
        event Action<IReadOnlyDictionary<string, RoomPlayer>> PlayersChanged;
        /// <summary>Another player's pose (uid, pose). Yours are not echoed back.</summary>
        event Action<string, PoseFrame> PoseReceived;
        /// <summary>
        /// A sentence of another player's answer, in order of <c>n</c> (which may skip numbers). Yours are not echoed back. Only sentences added after you joined are
        /// raised, so rejoining never replays an old answer.
        /// </summary>
        event Action<RoomAnswer> AnswerReceived;
        event Action<string, GameResult> GameResultReceived;
        /// <summary>You are out of the room: you left, it expired, or it closed because nobody was left. A host change is <see cref="RoomChanged"/>.</summary>
        event Action Closed;
    }

    /// <summary>
    /// The player's profile in Firestore <c>users/{uid}</c> (docs/contracts.md, section 3): names, language and the character's
    /// appearance, so a profile made on one device follows the player to another.
    /// </summary>
    public interface IProfileStore
    {
        /// <summary>The saved profile, or null if this account has none yet.</summary>
        Task<UserProfile> LoadAsync();
        /// <summary>Saves the profile. Completes once it is stored locally; it reaches the server when the network allows.</summary>
        Task SaveAsync(UserProfile profile);
        /// <summary>Raised when the profile changed on another device.</summary>
        event Action<UserProfile> ChangedElsewhere;
    }

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
        Task<string> GenerateAsync(string prompt, IReadOnlyList<CloudTool> tools, CancellationToken ct);
    }

    public interface IRouteLog
    {
        void Log(RouteEvent e);
    }
}
