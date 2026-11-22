using System;
using System.Threading;
using System.Threading.Tasks;

namespace HypaFirebase
{
    /// <summary>Firebase adapter boundary. Tokens must be fetched on demand, never persisted by gameplay.</summary>
    public interface IAuthSession
    {
        string UserId { get; }
        bool IsSignedIn { get; }
        event Action Changed;
        Task SignInAnonymouslyAsync(CancellationToken cancellationToken);
        Task<string> GetIdTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
        void SignOut();
    }
}
