namespace HypaFirebase.Rest
{
    /// <summary>
    /// The Firebase project the REST classes talk to. Every value is public client configuration, as in a
    /// google-services.json, except <see cref="AppCheckDebugToken"/>, which is registered in the console and must never be logged.
    /// </summary>
    public sealed class FirebaseRestConfig
    {
        public string ApiKey { get; }
        public string ProjectId { get; }
        public string AppId { get; }
        public string AppCheckDebugToken { get; }
        /// <summary>The Realtime Database address, for example https://my-project-default-rtdb.firebaseio.com</summary>
        public string DatabaseUrl { get; }

        public FirebaseRestConfig(string apiKey, string projectId, string appId, string appCheckDebugToken, string databaseUrl)
        {
            ApiKey = apiKey;
            ProjectId = projectId;
            AppId = appId;
            AppCheckDebugToken = appCheckDebugToken;
            DatabaseUrl = databaseUrl?.TrimEnd('/');
        }
    }
}
