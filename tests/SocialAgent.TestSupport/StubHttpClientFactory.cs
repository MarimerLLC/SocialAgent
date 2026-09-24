namespace SocialAgent.TestSupport;

/// <summary>
/// Hands out <see cref="HttpClient"/> instances over a single handler, mirroring how
/// <c>IHttpClientFactory</c> pools a handler behind short-lived clients.
/// </summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri? baseAddress = null)
    : IHttpClientFactory, IDisposable
{
    /// <summary>Creates a factory over a stub handler rooted at <paramref name="baseAddress"/>.</summary>
    public static StubHttpClientFactory For(StubHttpMessageHandler handler, string baseAddress = "https://example.test") =>
        new(handler, new Uri(baseAddress));

    public HttpClient CreateClient(string name) =>
        // disposeHandler: false — the handler is shared, exactly as the real factory does it.
        new(handler, disposeHandler: false) { BaseAddress = baseAddress };

    public void Dispose() => handler.Dispose();
}
