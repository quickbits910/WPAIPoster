using WPAIPoster.BlogPost;

namespace WPAIPoster.Tests;

public class BriefTagsTests
{
    [Fact]
    public void Parse_NoDirective_ReturnsEmptyTags_BriefUnchanged()
    {
        var (tags, brief) = BriefTags.Parse("A post about multi-agent collaboration.");
        Assert.Empty(tags);
        Assert.Equal("A post about multi-agent collaboration.", brief);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank_ReturnsEmpty(string? input)
    {
        var (tags, brief) = BriefTags.Parse(input);
        Assert.Empty(tags);
        Assert.Equal(input ?? string.Empty, brief);
    }

    [Fact]
    public void Parse_ExtractsCommaSeparatedTags_AndStripsDirective()
    {
        var (tags, brief) = BriefTags.Parse("Write about agents. [TAGS: Agent, Workflow, MCP] Make it punchy.");
        Assert.Equal(new[] { "Agent", "Workflow", "MCP" }, tags);
        Assert.Equal("Write about agents. Make it punchy.", brief);
    }

    [Fact]
    public void Parse_IsCaseInsensitive_AndTrimsWhitespace()
    {
        var (tags, _) = BriefTags.Parse("[tags:   Agent ,  Workflow  ]");
        Assert.Equal(new[] { "Agent", "Workflow" }, tags);
    }

    [Fact]
    public void Parse_DedupesCaseInsensitively_FirstSeenWins()
    {
        var (tags, _) = BriefTags.Parse("[TAGS: Agent, agent, AGENT, Workflow]");
        Assert.Equal(new[] { "Agent", "Workflow" }, tags);
    }

    [Fact]
    public void Parse_MultipleDirectives_AreMergedAndAllStripped()
    {
        var (tags, brief) = BriefTags.Parse("[TAGS: Agent] middle [TAGS: MCP, Agent] end");
        Assert.Equal(new[] { "Agent", "MCP" }, tags);
        Assert.Equal("middle end", brief);
    }

    [Fact]
    public void Parse_EmptyDirective_YieldsNoTags()
    {
        var (tags, brief) = BriefTags.Parse("before [TAGS: ] after");
        Assert.Empty(tags);
        Assert.Equal("before after", brief);
    }
}
