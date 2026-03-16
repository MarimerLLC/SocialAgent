using SocialAgent.Core.Models;

namespace SocialAgent.Core.Tests.Models;

[TestClass]
public class SocialPostTests
{
    [TestMethod]
    public void SocialPost_RequiredProperties_AreSet()
    {
        var post = new SocialPost
        {
            Id = "test:1",
            ProviderId = "test",
            PlatformPostId = "1",
            AuthorHandle = "user@test",
            Content = "Hello world",
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual("test:1", post.Id);
        Assert.AreEqual("test", post.ProviderId);
        Assert.AreEqual("1", post.PlatformPostId);
        Assert.AreEqual("user@test", post.AuthorHandle);
        Assert.AreEqual("Hello world", post.Content);
        Assert.AreEqual(0, post.LikeCount);
        Assert.AreEqual(0, post.RepostCount);
        Assert.AreEqual(0, post.ReplyCount);
        Assert.IsFalse(post.IsOwnPost);
    }

    [TestMethod]
    public void SocialPost_EngagementCounts_CanBeSet()
    {
        var post = new SocialPost
        {
            Id = "test:1",
            ProviderId = "test",
            PlatformPostId = "1",
            AuthorHandle = "user@test",
            Content = "Hello world",
            CreatedAt = DateTimeOffset.UtcNow,
            LikeCount = 10,
            RepostCount = 5,
            ReplyCount = 3,
            IsOwnPost = true
        };

        Assert.AreEqual(10, post.LikeCount);
        Assert.AreEqual(5, post.RepostCount);
        Assert.AreEqual(3, post.ReplyCount);
        Assert.IsTrue(post.IsOwnPost);
    }
}
