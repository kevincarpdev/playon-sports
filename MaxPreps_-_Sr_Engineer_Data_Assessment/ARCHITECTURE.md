# Architecture Overview

A single reference for how this is built and why. Read top to bottom to review the whole
system, or jump to a component.

Companion docs: [SEMANTIC_MODEL.md](SEMANTIC_MODEL.md) (the data contract),
[FINDINGS.md](FINDINGS.md) (what's wrong with the data and the model),
[PRODUCTION_NOTES.md](PRODUCTION_NOTES.md) (how this behaves on PlayOn's real data).

---

## 1. The one idea

**The model is an untrusted collaborator. It proposes; the system decides.**

Concretely, I inverted the usual text-to-SQL trust relationship:

> The model classifies **intent** and surfaces **entities**.
> The semantic layer owns the **SQL**.

Free-form model SQL is a guarded fallback for unrecognised intents, never the primary path.
That inversion is a direct response to evidence: five of the 17 recorded interpretations are
broken in five different ways, and three more execute cleanly while returning wrong numbers
(FINDINGS §2). Reported confidence on the broken ones ranges 0.88–0.95, so **the model's own
confidence cannot be the gate**.

Every response reports where its SQL came from (`sqlSource`) and, when we discarded the
model's version, why (`modelSqlRejectedBecause`).

---

## 2. Request flow

```
POST /ask  { question, slots? }
  │
  ├─ 0  PrincipalResolver ····· role from header → Principal              Security/
  ├─ 1  CapabilityRouter ······ complexity → tier → TierGrant ∩ RoleGrant Routing/
  ├─ 2  ILlmClient ············ InterpretAsync(question, semanticContext) Llm/
  ├─ 3  IntentCatalog ········· intent → plan: slots, capability, refusal Semantics/
  ├─ 4  SlotResolver ·········· fill required slots from text + lexicon   Semantics/
  │                             └─ unresolved → NEEDS_CLARIFICATION ✋
  ├─ 5  CertifiedQueries ······ intent + slots → reviewed SQL             Semantics/
  ├─ 6  SqlGuard ·············· STATIC validation, no DB access           Sql/
  ├─ 7  SqlExecutor ··········· read-only, row cap, timeout               Sql/
  ├─ 8  CaveatEngine ·········· ties, staleness, scope warnings           Quality/
  └─ 9  AskResponse ··········· discriminated outcome, never an exception Contracts/
```

`QuestionPipeline` is the only component that knows this order. Every stage is independently
testable and swappable — a real `ILlmClient` drops in with nothing else changing.

### Outcomes

| Outcome | HTTP | Meaning |
|---|---|---|
| `Answered` | 200 | Validated SQL ran. May carry `caveats[]`. |
| `NeedsClarification` | 200 | A required slot is missing/ambiguous. **Recoverable** — answer it and retry. |
| `CannotAnswer` | 422 | The data can't support it. Clarifying won't help. |
| `Error` | 500 | Our fault. Correlation id, no internals. |

The `NeedsClarification` / `CannotAnswer` split is load-bearing. The first means *we need more
from you*; the second means *no amount of clarification helps*. Clarifying the sport won't
conjure an injuries table.

---

## 3. Components

### `Configuration/SportsQaOptions.cs`
Every tunable in one place, bound from `appsettings.json`. Row caps, timeouts, confidence
thresholds, routing keyword lists, the default role. **No numeric or string literal thresholds
live in the pipeline** — retuning is a config change, not a rebuild.

### `Security/Authorization.cs` — who the AI is acting for
Five roles ordered by reach: `Anonymous` → `Member` → `Subscriber` → `Analyst` → `Admin`. Each
maps to a `RoleGrant` of permitted tables and a row ceiling.

This is not login/session auth (the brief discounts that, correctly). It's **policy on the
model's reach**, which is the same trust problem as SQL validation. Identity arrives as a
header resolved to a `Principal`; real OIDC replaces this one class.

The tier boundary is real and tested: `Anonymous` asking Tony Jackson's touchdowns gets
`table_not_permitted`; the same question as `Subscriber` answers 15. Two goldens pin both
sides.

### `Routing/CapabilityRouter.cs` — cost control, not security
Scores the question, picks a tier (`Lookup` / `Aggregate` / `Deep`), and returns capabilities
plus a row budget. The effective grant is `TierGrant ∩ RoleGrant` — least privilege.

One correction worth recording, because it's the kind of thing that only surfaces in testing:
my first version let the *text-derived tier* deny a legitimate intent. "How many points did
Jackson score" has no aggregate keyword and is 8 words, so it routed as `Lookup` and was
refused. Word count is a bad proxy. Now the tier is a **prior** and the resolved intent is
**evidence** — `EscalateFor` raises the tier once the intent is known.

The separation that makes this safe: **tier moves cost controls; role governs data access.**
`Capabilities.IsSecurityBoundary` marks the one capability (`OpsIntents`) that escalation may
never grant.

### `Data/SchemaCatalog.cs` — live schema + entity lexicon
Read once at startup, never per request. Two jobs:

1. **Identifier validation source.** Tables and columns come from the live catalog, so
   hallucinated identifiers die before execution and a new sport needs no code change.
2. **Entity lexicon** — players, schools, cities with their sports.

Two refinements the toy data forced, both of which are really *entity linking* rules:

- **Subsumption** — "Tony Jackson" beats the city "Jackson" when the longer name is present.
  Without it, every player whose surname is a place became spuriously ambiguous.
- **Shadowing** — precomputed at load. The city "Jackson" is contained by two longer names, so
  a bare "Jackson" must **never** auto-resolve to it. My first version silently resolved to the
  city, which would have returned zero rows and looked like a real answer.

This component is the seed of the highest-leverage production idea: the same index should back
site-search autocomplete and chatbot slot filling. See PRODUCTION_NOTES §4.

### `Data/DatasetFacts.cs` — coverage from the data
Sports and their seasons, read at startup. No hardcoded `'2025-26'` anywhere. Lets us resolve
sport → season deterministically, which is why we clarify **sport, not season** — one question
instead of two. `HasSingleSeasonPerSport` flags when that shortcut stops being safe.

### `Semantics/IntentCatalog.cs` — the policy table
Each intent maps to: required slots, required capability, and an optional permanent refusal.

Refusals live here rather than being inferred from a failed query, because **the reason
matters**. `team_injuries` is refused with "no injuries table exists" and what would be needed
— not retried. Ops intents are namespaced `ops:` and unprivileged callers get
`unsupported_question` rather than `forbidden`, so the internal tool surface isn't enumerable
by probing.

### `Semantics/Slots.cs` — slots and metrics
Slot names as constants. `Metric` is a closed set carrying an SQL expression, the sport it's
valid for, **and a direction**.

The direction field looks redundant — every metric here is higher-is-better. It isn't: track
and field makes every running event a time, and ranking those `DESC` silently returns the
*slowest* athlete as the leader. That bug looks entirely plausible in a result set.
PRODUCTION_NOTES §3.1.

Restricting metrics by sport is what structurally prevents summing football and basketball
points.

### `Semantics/SlotResolver.cs` — fill, or ask
Resolves each required slot from supplied answers, then question text, then the lexicon.
Anything unresolved becomes a `Clarification` with **grounded options** — candidates come from
the data, so we never offer an option that returns nothing.

Resolution order matters: **entity before sport**, because a resolved player pins the sport.
Asking "which sport?" about Marcus Bell is noise; he plays one. My first version asked anyway.

The four slots and the real question each rescues:

| Slot | Rescues |
|---|---|
| `sport` | "Did Riverside beat Oak Hill?" — **no** in football, **yes twice** in basketball |
| `entity` | "How many points did Jackson score?" — school, city, or player |
| `metric` | "Who is the best player?" — no defensible default exists |
| `school_a/b` | head-to-head needs two, matched in both orientations |

### `Semantics/CertifiedQueries.cs` — reviewed SQL, one template per intent
The heart of the correctness story. Each sharp edge is handled **once, correctly**, instead of
hoping the model remembers:

| Sharp edge | How the template handles it |
|---|---|
| `touchdowns` doesn't exist | uses `td` |
| Stale rollup | computes from `player_game_stats`, never `player_season_totals` |
| `LIMIT 1` on ties | `DENSE_RANK()`, returns the full tied set |
| Missing season filter | always applied, from `DatasetFacts` |
| One-directional head-to-head | matches home **and** away |
| Integer division | `* 1.0` on every rate |
| Orphan stat row | joins `games`, so scoping is correct |
| Nondeterministic order | unique tiebreaker as the last `ORDER BY` term |

Values arrive as **parameters**, never interpolated.

### `Sql/SqlGuard.cs` — static validation, the security boundary
Runs before the database is touched, so rejecting bad SQL costs nothing — which matters when
rejection is a *common* path. Allow-list, not blocklist: anything it doesn't positively
recognise is refused.

Checks: balanced quotes, single statement, `SELECT`/`WITH` only, no DDL/DML/`PRAGMA`/`ATTACH`,
every table and column resolved against the live catalog, every table permitted for the
caller's role, and a row limit enforced rather than requested.

String literals are stripped before identifier analysis, so `'Oak Hill'` is never mistaken for
an identifier and a literal containing a keyword can't trip the blocklist.

**Certified templates go through the same guard as model SQL.** Defence in depth, and it means
the role allow-list is applied on one code path that can't be forgotten on a branch.

### `Sql/SqlExecutor.cs` — bounded execution
Read-only connection (enforced at the connection string, not by convention), statement
timeout, and a row ceiling enforced *while reading* rather than trusted to the query. A
`SqliteException` is a normal reportable outcome — expected whenever the model invents schema
— not an incident.

### `Quality/CaveatEngine.cs` — honest qualifications
Our rules applied to our results. The model's `notes` field never reaches the caller.

`tied_result`, `no_matching_rows` (absence means *untracked*, not zero),
`uneven_schedules`, `sport_scoped`, `truncated`. Each maps to a documented sharp edge.

Tie detection uses the `RankedValueColumn` each template **declares**, rather than guessing
which column carries the answer — an earlier version keyed on a hardcoded name and silently
missed the highest-scoring-game tie.

### `Pipeline/QuestionPipeline.cs` + `SemanticContextProvider.cs`
The orchestrator, and the semantic model loaded once at startup as the system-prompt payload a
real model would receive. Loading it per request would be the hot path's biggest avoidable
cost; at scale it becomes a retrieval step returning only relevant sections.

---

## 4. Eval harness

`SportsQa.EvalRunner` runs **20 goldens** through the real pipeline **in-process** — no server,
no ports, no network. One command, exit code 0/1, so it gates CI.

Design choices that matter:

- **Expected values come from my own queries**, never the model. Each golden records its
  `groundTruthSql` so a reviewer can re-derive any number in one paste.
- **Organised by failure class**, not by question. 15 classes: stale rollups, hallucinated
  columns, unreported ties, entity ambiguity, sport ambiguity, subjectivity, authorization,
  out-of-scope, grain, and the clarification loop closing.
- **Per-assertion reporting.** A failure says *which property* drifted, with expected vs
  actual, the pipeline's intent/tier/sql-source, and the verification SQL.
- **Verified it can fail.** I flipped `marcus-bell-points` to 165 and `most-rebounds-tie` to
  `isTie: false` and confirmed exit code 1 with clear diffs. A suite that can't fail is
  worthless.

The three goldens I'd point at first:

1. `tony-jackson-touchdowns` → **15**. The model emits a nonexistent column; we answer anyway.
2. `marcus-bell-points` → **232, not 165**. Catches the stale rollup.
3. `riverside-oak-hill-football` vs `riverside-oak-hill-basketball` → **opposite answers**.
   Proves the slot layer is load-bearing rather than decorative.

---

## 5. Internal ops surface

Read-only, `Admin`-gated, returns 404 rather than 403 so it isn't discoverable:

- `GET /admin/rollup-freshness` — the two stale rollup rows, with `updated_at` vs the season's
  last game. This is the live data-quality defect in the dataset, so it's what operations
  actually needs.
- `GET /admin/schema` — tables, sports, seasons, and whether the single-season shortcut holds.

A management **UI** is deliberately absent: there's no frontend in this submission and the
brief lists UI under what it doesn't care about.

---

## 6. Scale and speed

What makes this fast, and what survives 10,000×:

- **Startup-cached** schema, coverage facts, and semantic model. Nothing re-read per request.
- **Static validation first** — the cheap path is the common path when the model misbehaves.
- **Bounded everything** — row caps, statement timeouts, read-only connections. One bad
  model query cannot take the API down.
- **Deterministic ordering** on every ranked query, so results are stable and cacheable.
- **Catalog-driven** identifier validation — new sports and seasons need no code change.
- **Config-driven budgets** — tunable per environment.

Deferred, and why they're the right next steps rather than omissions:

- **Result cache** keyed on `(normalised question, filled slots, role, schema version)`. Role
  *must* be in the key or a cache hit becomes privilege escalation.
- **Semantic-model retrieval** once it outgrows one prompt payload.
- **Unit tests on `SqlGuard`** with adversarial cases — it's the security boundary and
  currently has only end-to-end coverage.

Postgres mapping, partitioning, materialized rollups and row-level security are worked through
in PRODUCTION_NOTES §5.

---

## 7. Review checklist

If you're taking a fresh pass, these are the load-bearing claims to check:

- [ ] Trust inversion — `QuestionPipeline.ExecuteAsync`, `CertifiedQueries` class comment
- [ ] Model SQL still validated on the fallback path — `ExecuteAsync`, `query is null` branch
- [ ] Role allow-list unbypassable — `SqlGuard.ValidateIdentifiers`, applied to certified SQL too
- [ ] Tier can't deny a permitted intent — `CapabilityRouter.EscalateFor`
- [ ] Ops intents not enumerable — `QuestionPipeline`, `IsOpsIntent` branch
- [ ] Clarification loop closes — goldens `riverside-oak-hill-football` / `-basketball`
- [ ] No magic numbers — grep for literals in `Pipeline/`, `Sql/`, `Routing/`
- [ ] Goldens derived from data — every `groundTruthSql` in `goldens.json`
- [ ] Harness can fail — flip a golden's expected value and check exit code
