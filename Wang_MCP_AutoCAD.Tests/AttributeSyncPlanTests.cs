using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// The ATTSYNC decision. The test that matters most is the first one: a sync must keep each
/// reference's value by tag, or every title block in the drawing silently resets to "A1".
/// </summary>
public sealed class AttributeSyncPlanTests
{
    private static readonly AttributeDefault[] TwoTags =
    {
        new("TAG", "A1"),
        new("REV", "0"),
    };

    [Fact]
    public void PreserveValues_KeepsTheExistingValueForAMatchedTag()
    {
        ExistingAttribute[] existing = { new("TAG", "KEEP") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal("KEEP", plan.Attributes[0].Value);
        Assert.True(plan.Attributes[0].Preserved);
        Assert.Equal(1, plan.PreservedCount);
    }

    /// <summary>The Phase 4 acceptance scenario, in miniature: a new tag arrives with its default.</summary>
    [Fact]
    public void PreserveValues_NewTagGetsItsDefaultAndIsReportedAsAdded()
    {
        ExistingAttribute[] existing = { new("TAG", "KEEP") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal(2, plan.Attributes.Count);
        Assert.Equal("0", plan.Attributes[1].Value);
        Assert.False(plan.Attributes[1].Preserved);
        Assert.Equal(new[] { "REV" }, plan.AddedTags);
    }

    [Fact]
    public void WithoutPreservation_MatchedTagsResetToTheDefault()
    {
        ExistingAttribute[] existing = { new("TAG", "KEEP"), new("REV", "C") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: false);

        Assert.Equal("A1", plan.Attributes[0].Value);
        Assert.Equal("0", plan.Attributes[1].Value);
        Assert.Equal(2, plan.ResetCount);
        Assert.Equal(0, plan.PreservedCount);
        Assert.Empty(plan.AddedTags);
    }

    [Fact]
    public void TagsNoLongerInTheDefinition_AreRemoved()
    {
        ExistingAttribute[] existing = { new("TAG", "KEEP"), new("OLD", "gone") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal(new[] { "OLD" }, plan.RemovedTags);
        Assert.DoesNotContain(plan.Attributes, a => a.Value == "gone");
    }

    [Fact]
    public void TagMatching_IsCaseInsensitive()
    {
        ExistingAttribute[] existing = { new("tag", "KEEP") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal("KEEP", plan.Attributes[0].Value);
        Assert.Empty(plan.RemovedTags);
    }

    /// <summary>AutoCAD allows duplicate tags; they pair up in order rather than collapsing.</summary>
    [Fact]
    public void DuplicateTags_PairUpInOrder()
    {
        AttributeDefault[] definition = { new("LINE", ""), new("LINE", "") };
        ExistingAttribute[] existing = { new("LINE", "first"), new("LINE", "second") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(definition, existing, preserveValues: true);

        Assert.Equal("first", plan.Attributes[0].Value);
        Assert.Equal("second", plan.Attributes[1].Value);
    }

    [Fact]
    public void DuplicateTags_AnExtraExistingOneIsRemoved()
    {
        AttributeDefault[] definition = { new("LINE", "") };
        ExistingAttribute[] existing = { new("LINE", "first"), new("LINE", "second") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(definition, existing, preserveValues: true);

        Assert.Single(plan.Attributes);
        Assert.Equal("first", plan.Attributes[0].Value);
        Assert.Equal(new[] { "LINE" }, plan.RemovedTags);
    }

    [Fact]
    public void AttributesFollowDefinitionOrder_NotReferenceOrder()
    {
        ExistingAttribute[] existing = { new("REV", "C"), new("TAG", "KEEP") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal(0, plan.Attributes[0].DefinitionIndex);
        Assert.Equal("KEEP", plan.Attributes[0].Value);
        Assert.Equal(1, plan.Attributes[1].DefinitionIndex);
        Assert.Equal("C", plan.Attributes[1].Value);
    }

    /// <summary>A preserved empty value is still a value: "" must not be replaced by the default.</summary>
    [Fact]
    public void PreservedEmptyValue_StaysEmpty()
    {
        ExistingAttribute[] existing = { new("TAG", "") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(TwoTags, existing, preserveValues: true);

        Assert.Equal("", plan.Attributes[0].Value);
        Assert.True(plan.Attributes[0].Preserved);
    }

    [Fact]
    public void DefinitionWithNoAttributes_RemovesEverything()
    {
        ExistingAttribute[] existing = { new("TAG", "KEEP") };

        AttributeSyncPlan plan = AttributeSyncPlan.Build(Array.Empty<AttributeDefault>(), existing, preserveValues: true);

        Assert.Empty(plan.Attributes);
        Assert.Equal(new[] { "TAG" }, plan.RemovedTags);
    }
}
