namespace Jularr.Web.Features.ClientApi;

/// <summary>The addresses of the playback-intent client API, for the web pages that call it with their own session (instant-play.js).</summary>
public static class ClientApiPlaybackIntentRoutes
{
    public const string Intents = ClientApiContract.BasePath + "/video/playback-intents";

    /// <summary>The status read of one request; <c>{id}</c> is replaced by the request id, an optional <c>workEpisodeId</c> query names the episode waited for.</summary>
    public const string RequestStatus = ClientApiContract.BasePath + "/requests/{id}";
}
