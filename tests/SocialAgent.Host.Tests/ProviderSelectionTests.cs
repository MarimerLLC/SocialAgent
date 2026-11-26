using SocialAgent.Host;

namespace SocialAgent.Host.Tests;

[TestClass]
public class ProviderSelectionTests
{
    private static readonly string[] Production = ["mastodon", "bluesky"];
    private static readonly string[] WithThreads = ["mastodon", "bluesky", "threads"];

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("all")]
    [DataRow("ALL")]
    [DataRow("*")]
    [DataRow(" , ")]
    public void AbsentOrAll_MeansEveryProvider(string? requested)
    {
        var result = ProviderSelection.Resolve(requested, Production);

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Filter, "a null filter is the union of every provider");
    }

    [TestMethod]
    [DataRow("mastodon")]
    [DataRow("Mastodon")]
    [DataRow(" MASTODON ")]
    [DataRow("mastodon,mastodon")]
    public void OneProvider_IsMatchedCaseInsensitively_AndCanonicalised(string requested)
    {
        var result = ProviderSelection.Resolve(requested, Production);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("mastodon", result.Filter, "the repository filters by the provider's own id");
    }

    [TestMethod]
    [DataRow("mastodon,bluesky")]
    [DataRow("Bluesky, Mastodon")]
    [DataRow("mastodon,all")]
    [DataRow("bluesky,*")]
    public void ListNamingEveryProvider_MeansEveryProvider(string requested)
    {
        // RockBot's likely combined request, which used to return nothing at all.
        var result = ProviderSelection.Resolve(requested, Production);

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Filter);
    }

    [TestMethod]
    public void ListNamingOneProvider_MeansThatProvider()
    {
        var result = ProviderSelection.Resolve("bluesky,", WithThreads);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("bluesky", result.Filter);
    }

    [TestMethod]
    public void PartialSubset_IsAnError_BecauseTheRepositoryFiltersOneProvider()
    {
        var result = ProviderSelection.Resolve("mastodon,bluesky", WithThreads);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Error, "one provider or all of them");
        StringAssert.Contains(result.Error, "mastodon, bluesky, threads");
    }

    [TestMethod]
    [DataRow("twitter", "twitter")]
    [DataRow("mastodon,twitter", "twitter")]
    [DataRow("threads", "threads")]
    public void UnknownProvider_IsAnError_ListingTheValidIds(string requested, string unknown)
    {
        // "threads" is a real provider, but not an enabled one here.
        var result = ProviderSelection.Resolve(requested, Production);

        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Filter);
        StringAssert.Contains(result.Error, $"Unknown providerId '{unknown}'");
        StringAssert.Contains(result.Error, "Valid values: mastodon, bluesky");
        StringAssert.Contains(result.Error, "Omit providerId");
    }

    [TestMethod]
    [DataRow("all,twiter", "twiter")]
    [DataRow("mastodon,blusky,*", "blusky")]
    [DataRow("*,Mastodn", "Mastodn")]
    public void Typo_NextToAll_IsStillAnError(string requested, string unknown)
    {
        var result = ProviderSelection.Resolve(requested, Production);

        Assert.IsFalse(result.IsValid, "'all' must not hide an invalid id beside it");
        StringAssert.Contains(result.Error, $"Unknown providerId '{unknown}'");
    }

    [TestMethod]
    public void KnownProviderIds_AddsProvidersWithStoredRows_AfterTheEnabledOnes()
    {
        var known = ProviderSelection.KnownProviderIds(["mastodon", "bluesky"], ["threads", "bluesky", "aardvark"]);

        CollectionAssert.AreEqual(new[] { "mastodon", "bluesky", "aardvark", "threads" }, known.ToArray());
    }

    [TestMethod]
    public void DisabledProviderWithStoredRows_CanStillBeQueried()
    {
        // Threads was enabled once and its history is still stored, but it is disabled now.
        var known = ProviderSelection.KnownProviderIds(Production, ["mastodon", "threads"]);

        var result = ProviderSelection.Resolve("Threads", known);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("threads", result.Filter);
    }

    [TestMethod]
    public void ListOfTheEnabledProviders_IsNotWidened_WhenAnotherProviderHasStoredRows()
    {
        // A null filter would return the stored Threads rows too, which the caller left out.
        var known = ProviderSelection.KnownProviderIds(Production, ["threads"]);

        var named = ProviderSelection.Resolve("mastodon,bluesky", known);
        Assert.IsFalse(named.IsValid, "the repository cannot filter to a subset, so this must not become 'all'");
        StringAssert.Contains(named.Error, "one provider or all of them");

        var everything = ProviderSelection.Resolve("mastodon,bluesky,threads", known);
        Assert.IsTrue(everything.IsValid);
        Assert.IsNull(everything.Filter);
    }

    [TestMethod]
    public void NoEnabledProviders_RejectsAnId_ButAllowsAll()
    {
        var rejected = ProviderSelection.Resolve("mastodon", []);
        Assert.IsFalse(rejected.IsValid);
        StringAssert.Contains(rejected.Error, "no social media providers are enabled");

        Assert.IsTrue(ProviderSelection.Resolve(null, []).IsValid);
    }

    [TestMethod]
    public void SingleEnabledProvider_NamedExplicitly_KeepsTheFilter()
    {
        var result = ProviderSelection.Resolve("Bluesky", ["bluesky"]);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("bluesky", result.Filter);
    }
}
