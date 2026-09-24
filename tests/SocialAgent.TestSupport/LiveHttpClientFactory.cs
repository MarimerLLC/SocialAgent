namespace SocialAgent.TestSupport;

/// <summary>
/// Real-network factory for the opt-in <c>Integration</c> tests, which talk to live provider APIs.
/// </summary>
public sealed class LiveHttpClientFactory(string baseAddress) : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client = new() { BaseAddress = new Uri(baseAddress) };

    public HttpClient CreateClient(string name) => _client;

    public void Dispose() => _client.Dispose();
}
