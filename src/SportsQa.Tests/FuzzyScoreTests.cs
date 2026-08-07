using SportsQa.Api.Data;
using Xunit;

namespace SportsQa.Tests;

/// <summary>
/// Score-only tests for fuzzy entity resolution. SlotResolver auto-resolves when score
/// clears Trust.MinSlotConfidence (default 0.75) with gap ≥ Execution.FuzzyAutoResolveMinGap
/// (0.1). These assert the scorer still ranks strong typos well above that floor.
/// </summary>
public sealed class FuzzyScoreTests
{
    [Fact]
    public void Exact_phrase_scores_one()
    {
        Assert.Equal(1.0, SchemaCatalog.ScoreMention(
            "How many games did Jackson Prep win?", "Jackson Prep"));
    }

    [Fact]
    public void Typo_jackson_prep_is_auto_resolvable()
    {
        var score = SchemaCatalog.ScoreMention(
            "How many games did Jakson Prep win in football?", "Jackson Prep");
        Assert.True(score >= 0.9, $"expected >= 0.9, got {score}");
    }

    [Fact]
    public void Glued_oakhill_is_auto_resolvable()
    {
        var score = SchemaCatalog.ScoreMention(
            "How many players are on the OakHill football roster?", "Oak Hill");
        Assert.True(score >= 0.9, $"expected >= 0.9, got {score}");
    }

    [Fact]
    public void Truncated_marcus_bel_is_auto_resolvable()
    {
        var score = SchemaCatalog.ScoreMention(
            "How many total points has marcus bel scored this season?", "Marcus Bell");
        Assert.True(score >= 0.9, $"expected >= 0.9, got {score}");
    }

    [Fact]
    public void Near_tie_gap_is_below_threshold()
    {
        // A bare surname typo sits equally close to the school and the player — gap under 0.1
        // so SlotResolver must clarify rather than guess.
        var prep = SchemaCatalog.ScoreMention("games at Jakson", "Jackson Prep");
        var tony = SchemaCatalog.ScoreMention("games at Jakson", "Tony Jackson");
        var gap = Math.Abs(prep - tony);
        Assert.True(gap < 0.1,
            $"expected gap < 0.1 between Jackson Prep ({prep}) and Tony Jackson ({tony})");
    }

    [Fact]
    public void Bare_jackson_exact_does_not_outrank_via_typo_alone()
    {
        // Exact "Jackson" is 1.0 for the city name; longer names only get partial credit from
        // the bare token. Auto-resolve still depends on shadowing in SlotResolver.
        Assert.Equal(1.0, SchemaCatalog.ScoreMention("How many points did Jackson score?", "Jackson"));
        var prep = SchemaCatalog.ScoreMention("How many points did Jackson score?", "Jackson Prep");
        var tony = SchemaCatalog.ScoreMention("How many points did Jackson score?", "Tony Jackson");
        Assert.True(prep < 0.9, $"Jackson Prep should not auto-resolve from bare Jackson, got {prep}");
        Assert.True(tony < 0.9, $"Tony Jackson should not auto-resolve from bare Jackson, got {tony}");
    }

    [Fact]
    public void Exact_outranks_typo()
    {
        var exact = SchemaCatalog.ScoreMention("Jackson Prep won", "Jackson Prep");
        var typo = SchemaCatalog.ScoreMention("Jakson Prep won", "Jackson Prep");
        Assert.True(exact > typo);
        Assert.Equal(1.0, exact);
    }
}
