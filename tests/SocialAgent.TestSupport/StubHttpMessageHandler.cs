using System.Net;
using System.Text;

namespace SocialAgent.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a caller-supplied routine and records
/// every request it saw, so tests can assert on paging URLs and auth headers.
/// </summary>
public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];
    private int _callCount;

    /// <summary>Requests in the order they were sent.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>Replies to every request with the same JSON body and a 200.</summary>
    public static StubHttpMessageHandler AlwaysJson(string json) =>
        new((_, _) => Json(HttpStatusCode.OK, json));

    /// <summary>Replies with each response in turn; the last one repeats once the queue is spent.</summary>
    public static StubHttpMessageHandler Sequence(params Func<HttpResponseMessage>[] responses) =>
        new((_, call) => responses[Math.Min(call, responses.Length - 1)]());

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        int call;
        lock (_requests)
        {
            call = _callCount++;
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));
        }

        return responder(request, call);
    }

    public sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthScheme,
        string? AuthParameter,
        string? Body)
    {
        public string PathAndQuery => Uri.PathAndQuery;
    }
}
