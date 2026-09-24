namespace SocialAgent.Host.Routing;

public class SkillRouterOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Ceiling on the routing call. This sits on the A2A request path, so a slow model must not
    /// hold a caller open — routing falls back to keyword matching once this elapses.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;
}
