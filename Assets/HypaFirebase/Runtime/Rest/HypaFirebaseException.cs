using System;

namespace HypaFirebase.Rest
{
    /// <summary>A Firebase REST call failed. The message names the error code Firebase sent, never a token.</summary>
    public sealed class HypaFirebaseException : Exception
    {
        public int StatusCode { get; }
        public HypaFirebaseException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    }
}
