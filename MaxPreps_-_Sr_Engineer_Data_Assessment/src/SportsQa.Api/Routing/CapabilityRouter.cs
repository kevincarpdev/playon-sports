using SportsQa.Api.Configuration;
using SportsQa.Api.Security;

namespace SportsQa.Api.Routing;

/// <summary>
/// What a query is permitted to do structurally. Granting capabilities rather than trusting
/// SQL shape means an escalation has to be deliberate, not accidental.
/// </summary>
[Flags]
public enum Capability
{
    None = 0,
    Schema = 1 << 0,
    ScalarQuery = 1 << 1,
    AggregateQuery = 1 << 2,
    RankedQuery = 1 << 3,
    JoinAcrossGames = 1 << 4,
    OpsIntents = 1 << 5,
}

/// <summary>Complexity tiers. A cheap question should not pay for deep reasoning.</summary>
public enum ModelTier
{
    Lookup,
    Aggregate,
    Deep,
}

public sealed record RoutingDecision(
    ModelTier Tier,
    Capability Capabilities,
    IReadOnlySet<string> AllowedTables,
    int MaxRows)
{
    public bool Grants(Capability required) => (Capabilities & required) == required;
}

/// <summary>
/// Which capabilities are a security boundary rather than a cost control. Everything else is
/// a budget decision the router may revise once the intent is known.
/// </summary>
public static class Capabilities
{
    public static bool IsSecurityBoundary(Capability capability) =>
        capability.HasFlag(Capability.OpsIntents);
}

/// <summary>
/// Scores the question, picks a tier, then intersects the tier's capabilities with the
/// caller's role grant. The effective grant is the *lesser* of the two — least privilege.
/// </summary>
public sealed class CapabilityRouter(SportsQaOptions options)
{
    public RoutingDecision Route(string question, Principal principal)
    {
        var tier = ClassifyTier(question);
        var roleGrant = RoleGrants.For(principal.Role, options.Execution);

        var capabilities = CapabilitiesFor(tier);
        if (roleGrant.AllowOpsIntents)
        {
            capabilities |= Capability.OpsIntents;
        }

        return new RoutingDecision(
            tier,
            capabilities,
            roleGrant.Tables,
            Math.Min(roleGrant.MaxRows, options.Execution.MaxRows));
    }

    /// <summary>
    /// Revises the tier once the intent is known. Classifying from question text alone is a
    /// prior, not evidence — "how many points did Jackson score" is an aggregate question with
    /// no aggregate keyword in it. So a resolved intent may raise the tier it was routed to.
    ///
    /// This only moves cost controls. Data access stays governed by the role's table
    /// allow-list, which <see cref="Sql.SqlGuard"/> enforces on every query.
    /// </summary>
    public RoutingDecision EscalateFor(RoutingDecision decision, Capability required)
    {
        if (decision.Grants(required) || Capabilities.IsSecurityBoundary(required))
        {
            return decision;
        }

        var tier = (ModelTier)Math.Max((int)decision.Tier, (int)ModelTier.Aggregate);
        return decision with
        {
            Tier = tier,
            Capabilities = CapabilitiesFor(tier) | (decision.Capabilities & Capability.OpsIntents),
        };
    }

    private ModelTier ClassifyTier(string question)
    {
        var lowered = $" {question.ToLowerInvariant()} ";
        var routing = options.Routing;

        if (routing.DeepSignals.Any(lowered.Contains))
        {
            return ModelTier.Deep;
        }

        if (routing.AggregateSignals.Any(signal => lowered.Contains($" {signal}")))
        {
            return ModelTier.Aggregate;
        }

        var words = question.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words > routing.LookupMaxWords ? ModelTier.Aggregate : ModelTier.Lookup;
    }

    private static Capability CapabilitiesFor(ModelTier tier) => tier switch
    {
        ModelTier.Lookup =>
            Capability.Schema | Capability.ScalarQuery,

        ModelTier.Aggregate =>
            Capability.Schema | Capability.ScalarQuery | Capability.AggregateQuery |
            Capability.RankedQuery | Capability.JoinAcrossGames,

        ModelTier.Deep =>
            Capability.Schema | Capability.ScalarQuery | Capability.AggregateQuery |
            Capability.RankedQuery | Capability.JoinAcrossGames,

        _ => Capability.None,
    };
}
