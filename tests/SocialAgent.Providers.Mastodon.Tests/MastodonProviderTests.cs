namespace SocialAgent.Providers.Mastodon.Tests;

[TestClass]
public class MastodonProviderTests
{
    [TestMethod]
    public void MastodonOptions_DefaultInstanceUrl_IsSet()
    {
        var options = new MastodonOptions();

        Assert.AreEqual("https://mastodon.social", options.InstanceUrl);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(string.Empty, options.AccessToken);
    }
}
