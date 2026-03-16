namespace SocialAgent.Providers.Bluesky.Tests;

[TestClass]
public class BlueskyProviderTests
{
    [TestMethod]
    public void BlueskyOptions_DefaultServiceUrl_IsSet()
    {
        var options = new BlueskyOptions();

        Assert.AreEqual("https://bsky.social", options.ServiceUrl);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(string.Empty, options.Handle);
        Assert.AreEqual(string.Empty, options.AppPassword);
    }
}
