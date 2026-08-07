using SportsQa.Api.Configuration;
using SportsQa.Api.Security;

namespace SportsQa.Api.Routing;

/// <summary>
/// Structural query shapes intents may declare. Kept on IntentPlan as documentation of what
/// each intent needs; authorization is role tables / ops / row cap, not these flags.
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

/// <summary>
/// TODO(contract): Route returns tables + maxRows + AllowOps only. No tier ladder.
/// Subscription decides what may be read; SqlGuard enforces the table allow-list.
/// </summary>
public sealed record RoutingDecision(
    IReadOnlySet<string> AllowedTables,
    int MaxRows,
    bool AllowOps);

/// <summary>
/// Maps the caller's role to the effective grant: table allow-list, row ceiling, and ops.
/// </summary>
public sealed class CapabilityRouter(SportsQaOptions options)
{
    public RoutingDecision Route(Principal principal)
    {
        var roleGrant = RoleGrants.For(principal.Role, options.Execution);

        return new RoutingDecision(
            roleGrant.Tables,
            Math.Min(roleGrant.MaxRows, options.Execution.MaxRows),
            roleGrant.AllowOpsIntents);
    }
}
