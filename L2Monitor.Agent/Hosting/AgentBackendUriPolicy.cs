namespace L2Monitor.Agent.Hosting;

internal static class AgentBackendUriPolicy
{
    public static bool TryResolve(string? value, out Uri backendBaseUri)
    {
        backendBaseUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || (candidate.Scheme != Uri.UriSchemeHttps
                && (candidate.Scheme != Uri.UriSchemeHttp || !candidate.IsLoopback)))
        {
            return false;
        }

        backendBaseUri = candidate;
        return true;
    }
}
