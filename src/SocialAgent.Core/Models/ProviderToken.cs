namespace SocialAgent.Core.Models;

public class ProviderToken
{
    public required string ProviderId { get; set; }
    public required string AccessToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
