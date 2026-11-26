namespace SocialAgent.Host.Services;

/// <summary>One provider's most recent poll: whether it worked, and when it ran.</summary>
internal sealed record ProviderPollOutcome(bool Succeeded, DateTimeOffset At);

/// <summary>
/// Each provider's last poll outcome, recorded by the polling loop. The <c>social://providers</c>
/// resource reports these, so its content moves exactly when the loop announces a flip.
/// </summary>
internal sealed class ProviderPollOutcomes
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProviderPollOutcome> _outcomes = new(StringComparer.Ordinal);

    /// <summary>Records a poll; true when it flipped between working and failing (not on the first poll).</summary>
    public bool Record(string providerId, bool succeeded, DateTimeOffset at)
    {
        lock (_lock)
        {
            var flipped = _outcomes.TryGetValue(providerId, out var previous) && previous.Succeeded != succeeded;
            _outcomes[providerId] = new ProviderPollOutcome(succeeded, at);
            return flipped;
        }
    }

    /// <summary>The provider's last poll, or null before its first.</summary>
    public ProviderPollOutcome? Get(string providerId)
    {
        lock (_lock)
        {
            return _outcomes.GetValueOrDefault(providerId);
        }
    }
}
