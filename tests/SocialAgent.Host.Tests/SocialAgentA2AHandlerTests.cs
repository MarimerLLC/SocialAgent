using System.Text.Json;
using SocialAgent.Host;

namespace SocialAgent.Host.Tests;

[TestClass]
public class SocialAgentA2AHandlerTests
{
    private static Dictionary<string, JsonElement> Metadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [TestMethod]
    public void EnvelopeParameters_AreRead_WhenTheMessageHasNone()
    {
        // The skill id has always been read from the envelope too; its parameters were dropped,
        // which silently widened providerId to every provider.
        var merged = SocialAgentA2AHandler.MergeParameters(null, Metadata("""{"skill":"recent-mentions","providerId":"bluesky"}"""));

        Assert.IsNotNull(merged);
        Assert.AreEqual("bluesky", merged["providerId"].GetString());
    }

    [TestMethod]
    public void MessageParameters_WinOverTheEnvelope()
    {
        var merged = SocialAgentA2AHandler.MergeParameters(
            Metadata("""{"providerId":"mastodon"}"""),
            Metadata("""{"providerId":"bluesky","count":5}"""));

        Assert.IsNotNull(merged);
        Assert.AreEqual("mastodon", merged["providerId"].GetString(), "the message is the more specific source");
        Assert.AreEqual(5, merged["count"].GetInt32(), "envelope-only keys are kept");
    }

    [TestMethod]
    public void NoMetadata_StaysNull()
    {
        Assert.IsNull(SocialAgentA2AHandler.MergeParameters(null, null));
        Assert.IsNull(SocialAgentA2AHandler.MergeParameters(null, []));
    }
}
