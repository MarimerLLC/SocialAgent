using SocialAgent.Core.Text;

namespace SocialAgent.Core.Tests.Text;

[TestClass]
public class HtmlTextTests
{
    [TestMethod]
    public void StripsTags()
    {
        Assert.AreEqual("Hello there", HtmlText.ToPlainText("<p>Hello <b>there</b></p>"));
    }

    [TestMethod]
    public void DecodesEntities()
    {
        Assert.AreEqual("Tom & Jerry <3", HtmlText.ToPlainText("<p>Tom &amp; Jerry &lt;3</p>"));
    }

    [TestMethod]
    public void PreservesLineBreaks()
    {
        Assert.AreEqual("Line one\nLine two", HtmlText.ToPlainText("<p>Line one<br>Line two</p>"));
        Assert.AreEqual("Line one\nLine two", HtmlText.ToPlainText("<p>Line one<br />Line two</p>"));
    }

    [TestMethod]
    public void SeparatesParagraphs()
    {
        Assert.AreEqual("First\n\nSecond", HtmlText.ToPlainText("<p>First</p><p>Second</p>"));
    }

    [TestMethod]
    public void KeepsMentionAndHashtagText()
    {
        var html = """<p>Hi <a href="https://m.test/@bob" class="mention">@bob</a> <a href="https://m.test/tags/dotnet">#dotnet</a></p>""";

        Assert.AreEqual("Hi @bob #dotnet", HtmlText.ToPlainText(html));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankInput_ReturnsEmpty(string? input)
    {
        Assert.AreEqual(string.Empty, HtmlText.ToPlainText(input));
    }

    [TestMethod]
    public void PlainText_PassesThroughUnchanged()
    {
        Assert.AreEqual("already plain", HtmlText.ToPlainText("already plain"));
    }
}
