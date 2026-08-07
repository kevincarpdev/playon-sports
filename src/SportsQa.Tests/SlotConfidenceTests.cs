using SportsQa.Api.Configuration;
using SportsQa.Api.Data;
using SportsQa.Api.Semantics;
using Xunit;

namespace SportsQa.Tests;

/// <summary>
/// Proves <see cref="TrustOptions.MinSlotConfidence"/> gates fuzzy slot fills: the same
/// mid-score typo answers at 0.75 and clarifies at 0.95.
/// </summary>
public sealed class SlotConfidenceTests
{
    private const string MidScoreTypo =
        "How many games did Jaksn Prep win in the 2025 football season?";

    [Fact]
    public void Mid_score_typo_fills_at_default_min_slot_confidence()
    {
        var score = SchemaCatalog.ScoreMention(MidScoreTypo, "Jackson Prep");
        Assert.InRange(score, 0.75, 0.95);

        var slots = Resolve(MidScoreTypo, minSlotConfidence: 0.75);

        Assert.True(slots.IsComplete);
        Assert.Equal("Jackson Prep", slots.Values[Slots.Entity]);
        Assert.Equal("Football", slots.Values[Slots.Sport]);
    }

    [Fact]
    public void Mid_score_typo_clarifies_when_min_slot_confidence_raised()
    {
        var score = SchemaCatalog.ScoreMention(MidScoreTypo, "Jackson Prep");
        Assert.InRange(score, 0.75, 0.95);

        var slots = Resolve(MidScoreTypo, minSlotConfidence: 0.95);

        Assert.False(slots.IsComplete);
        Assert.Contains(slots.Clarifications, c => c.Slot == Slots.Entity);
        Assert.DoesNotContain(Slots.Entity, slots.Values.Keys);
    }

    private static SlotResolution Resolve(string question, double minSlotConfidence)
    {
        var options = new SportsQaOptions
        {
            DatabasePath = TestPaths.Database,
            FakeLlmResponsesPath = TestPaths.FakeLlmResponses,
            SemanticModelPath = TestPaths.SemanticModel,
            Trust = new TrustOptions { MinSlotConfidence = minSlotConfidence },
        };

        var catalog = SchemaCatalog.Load(options);
        var facts = DatasetFacts.Load(options);
        var resolver = new SlotResolver(catalog, facts, options);
        return resolver.Resolve(question, IntentCatalog.For("team_wins"), provided: null);
    }
}
