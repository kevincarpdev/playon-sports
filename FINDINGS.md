# Findings

What I learned about this dataset and this model. Everything here was verified with my own
queries. The SQL to re-derive each claim is in `src/SportsQa.EvalRunner/goldens.json` under
`groundTruthSql`.

Where a finding has a direct analogue in MaxPreps production data, I've noted it, the
interesting ones aren't toy problems, they're the same problems at 1/100,000 scale. Those
analogues are worked through properly in [PRODUCTION_NOTES.md](PRODUCTION_NOTES.md).

---

## 1. Data-quality issues

### 1.1 `player_season_totals` is stale, on exactly the two players the questions ask about

The headline finding. The rollup disagrees with the fact table for two of its 120 rows:

| Player | Rollup | Truth | Drift | `updated_at` | Season ended |
|---|---|---|---|---|---|
| Marcus Bell (`113`, BB) | 165 pts / 7 GP | **232 / 9** | −67 pts, −2 GP | 2026-02-10 | 2026-02-24 |
| Tony Jackson (`14`, FB) | 54 pts / 5 GP | **90 / 7** | −36 pts, −2 GP | 2025-10-10 | 2025-10-17 |

Those two players are the subjects of three of the 17 supported questions. That is not a
coincidence, the rollup is *convenient*, the model reaches for it explicitly "for
efficiency", and it is wrong.

It is missing whole games rather than being slightly off, so no tolerance threshold saves
you. The only defence is to compute from `player_game_stats` and treat the rollup as a cache
that must prove its freshness. `GET /admin/rollup-freshness` reports the two stale rows.

> **Production analogue:** MaxPreps stats are coach-entered and corrected after the fact, so
> the fact table itself is retroactively mutable. This gets *worse* at scale, not better.

### 1.2 One stat row references a game that does not exist

`player_game_stats.game_id` is declared with **no foreign key**, and one row exploits it:

```
stat_id=1033  player_id=160 (Silas York, Westbrook basketball)  game_id=9999  →  no such game
```

What makes this genuinely nasty is that **both** handlings are silent:

- Join `games` to scope sport/season → the row vanishes, losing 12 pts / 3 reb / 1 ast.
- Don't join → the row counts, attributed to a phantom game.

Two defensible queries, two different totals, no error either way. I chose to join `games`
(correct scoping matters more than one row) and to define per-player game counts as "games
with a resolvable stat line". Row counts confirm it: 1033 total, 1032 joinable.

### 1.3 `rebounds` and `assists` are clipped, I don't think this one was planted

Not on the list of things I expected. `rebounds` never exceeds 12 and `assists` never exceed
9, and both **pile up at the ceiling** instead of tapering:

| rebounds | 12 | 11 | 10 | 9 | 8 |
|---|---|---|---|---|---|
| rows | **52** | 40 | 51 | 50 | 60 |

Compare `points`, which has a proper tail: exactly one row each at 35, 32, 30, 26, 25.

So rebounds/assists are capped or bucketed upstream. The consequence is sharper than "there's
a tie": **"who had the most rebounds in a single game" has no meaningful answer.** 52 stat
lines across 34 players share the maximum. Any system returning one name is fabricating a
distinction the data does not contain.

I return the full tied set with a `tied_result` caveat. Arguably it should refuse outright.
I documented that judgement call rather than silently picking.

### 1.4 40 of 160 players have no stat rows at all, and it's structural

The untracked players are entirely football `OL` (16), `TE` (8), `DB` (8), `LB` (8). Every
offensive lineman and every defensive player. Only `QB`, `RB`, `WR` and `K` are tracked.

So absence of a stat line means **untracked**, not zero. "How many touchdowns did our left
tackle score?" must answer "not tracked". And roster size (12) is a different question from
tracked players (**7** per football team: 1 QB, 2 RB, 3 WR, 1 K).

This one is real: MaxPreps genuinely does not carry individual OL statistics.

### 1.5 Schedules are badly uneven, which distorts every season total

Games played per team: basketball ranges **8 to 14** (Westbrook 8, Jackson Prep 14, Riverside
13, everyone else 9); football 7 to 8.

Marcus Bell leads basketball scoring with 232 points in **9** games while Eli Quigley has 222
in **14**. On a per-game basis Bell (25.8) is far ahead of Quigley (15.9), but a naive
"total points" leaderboard makes them look close. Any cross-team total comparison needs the
games-played denominator attached, which is why `uneven_schedules` is a caveat.

### 1.6 Player points reconcile to team scores, for 136 of 138 team-sides

Worth stating because it licenses a class of queries, and worth stating precisely because my
first version of this finding overclaimed it.

There are **138 team-sides** across 69 games. **136** have stat lines, and in all 136 the summed
player `points` equals that team's score exactly, zero mismatches, no double-counting, despite
40 untracked players.

The two that don't are both sides of **game 69** (Football, Harbor View 7 – Jackson Prep 14),
which has final scores and **zero** stat rows. So scoring attribution is *reliable where stat
lines exist* and *silently absent for one game*. I originally wrote "all 136 team-games", which
quietly redefined the denominator as the subset that worked. An adversarial review caught it.

The practical consequence: a team total built from player rows under-reports for game 69, and
`SUM` over no rows returns `NULL`, not `0`.

Football `points` is derived: `td * 6` for QB/RB/WR, and independent kicking points (0–8,
`td` always 0) for K. So `SUM(td)` and `SUM(points)` measure different things, and a kicker
has points with zero touchdowns.

### 1.7 Four basketball games are drawn, and I had claimed none were

The worst error I made. `SEMANTIC_MODEL.md` §3 asserted "No ties exist in either sport's
results." Four basketball games are tied: 96–96, 100–100, 83–83, 83–83.

That is a false statement in the deliverable meant to be a model's source of truth, and it
teaches exactly the wrong lesson: a model trusting it would never handle `home_score =
away_score`, and would never caveat a record.

It also revealed a real bug in my own code. `HeadToHead` computed
`CASE WHEN home_score > away_score THEN home ELSE away END AS winner`, which on a draw **names
the away team as the winner**. Fixed to three branches returning `NULL` for a tie. No supported
question happens to hit a drawn game, so no golden caught it, which is the point.

`TeamWins` was already correct by luck: it uses `>` so draws are excluded rather than
miscounted, though it silently drops them from any record accounting.

### 1.8 The rollup inherits the orphan-row ambiguity

Silas York's rollup says **43** points over 8 games. The raw fact-table sum is **55** over 9
rows; joined to `games`, it is **43** over 8. So the nightly job evidently joins `games` and
drops the orphan (§1.2) too.

This means "the rollup disagrees with the fact table" is not one comparison but two, and which
answer is right depends on a join policy nobody wrote down.

### 1.9 `updated_at` is a freshness signal, not a correctness signal

Marcus Bell's rollup carries `updated_at = 2026-02-10`, which is **after** his own last game
(`2026-02-06`), and it is still wrong: 165 points over 7 games against a true 232 over 9.

So a timestamp post-dating a player's final appearance proves nothing. My `/admin/rollup-freshness`
check compares `updated_at` against the *season's* last game (2026-02-24), which flags Bell
correctly, but only by accident of the season running longer than his schedule. A rollup that
is stale for a player whose season ended early would pass that check.

The honest framing: freshness detection is a floor, not a correctness proof. The only reliable
check is recomputing from the fact table and diffing.

### 1.10 `player_season_totals` cannot represent a two-sport athlete

Primary key is `(player_id, season)` with `sport` as a *non-key* column. Two sports with
distinct season strings ('2025', '2025-26') hide the flaw. It breaks the first time one
athlete has rows for two sports under the same season string, which is routine for real
high-school athletes.

---

## 2. Model interpretations I distrust

I executed all 17 recorded queries against `sports.db` rather than reading them. The tally:
**three throw, four are correct, and ten run cleanly with a defect.** Reported confidence across
the thirteen defective ones runs 0.88–0.97, and the two highest-confidence queries in the entire
set after `count_teams` both carry latent scope bugs. That is the whole lesson: **self-reported
confidence is uncorrelated with correctness and cannot be the gate.**

### The three that throw

| Question | Model confidence | Error |
|---|---|---|
| Tony Jackson touchdowns | 0.91 | `SUM(s.touchdowns)`, **column does not exist** (it's `td`). |
| Riverside injuries | 0.88 | `FROM injuries`, **table does not exist**. Confident SQL over nothing. |
| Did Riverside beat Oak Hill | 0.90 | `'Riverside AND a.school = '`, **unbalanced quote**, syntax error. |

These are the *safe* failures. They fail loudly, so a caller cannot mistake them for an answer.
Head-to-head is worth a second look though: fix the quote and two further defects surface behind
it, because it also checks **Riverside as home only** (missing 2 of 3 meetings) and has **no
sport filter at all**. One recorded query, three independent defects.

### The ten that execute and are still wrong

**Marcus Bell's points and PPG (0.93–0.94)** read the stale rollup, "using the pre-aggregated
season totals table for efficiency". Returns 165 and 23.57 instead of 232 and 25.78. This is
the most dangerous category: plausible SQL, plausible number, no error, wrong answer.

**Two superlatives use `LIMIT 1` over tied data**, and a third cuts a tie at the boundary:
most rebounds (52-way tie at 12), highest-scoring football game (2 games tied at 73, Central
Valley 38–35 Lakewood and Riverside 35–38 Lakewood), and the top-5 scorer list, where ranks 4
and 5 are **both 211** so the cut is arbitrary. The model returns one arbitrary row as fact. Its
highest-scoring-game query also returns `game_id` with **no team names**, useless to a fan even
when correct.

**A silent entity guess (0.90).** "How many points did Jackson score?" resolves to Jackson Prep
*football* with no acknowledgement that Jackson Prep basketball, the city of Jackson, and Tony
Jackson all match, same class as the rollup: plausible SQL, plausible number, wrong question
answered.

**"Who is the best player" (0.95)** asserts that best means most total points. That is a
subjective definition the model invented, and it is summed across both sports on top.

**"Who scored the most points this season" (0.92)** sums `points` across both sports. That it
currently returns a basketball player is luck, not correctness.

Three more that are latent rather than wrong *today*:

- **Derek Foss's passing yards (0.96)** has no season or sport scope. Correct now because he
  appears in one season.
- `top5_scorers_basketball` (0.95) omits the season filter. Fine with one basketball season;
  wrong the day a second lands.
- `schools_both_sports` (0.97) uses `HAVING COUNT(DISTINCT sport) = 2`. Breaks on a third sport.

That is ten distinct queries: two stale-rollup reads, three mishandled ties (one of which is
also unscoped), one entity guess, two invented or cross-sport metrics, and two more that are
correct only by accident of the current data. The remaining four, `count_teams`,
`top_scorer_basketball`, `roster_count` and `team_wins`, I checked against my own SQL and they
are right.

### What I missed on the first pass

Recorded because it's the honest version. An adversarial review of this section found five
defects I had characterised as product ambiguity rather than model defects, or omitted:

1. **"Jackson" as a model defect**, not just an ambiguity I chose to clarify. The recorded SQL
   is a silent guess that executes, that belongs in this table.
2. **Head-to-head missing the sport filter** as its own defect, separate from the quote and the
   orientation.
3. **The top-5 tie at 211.** I documented it in the semantic model and left it out of this list.
4. **Derek Foss missing season scope**, same latent class as the top-5 query.
5. **Score ties and game 69**, absent entirely, and §1.6 actively papered over the second.

I also wrote "three superlatives" and then listed two.

A later verification pass caught the same class of error twice more in this very section. I had
written "five of the 17 interpretations are broken" over a table whose five rows covered only
four questions, and "plus three that execute cleanly" over a list of five. So I re-derived the
tally the only way that settles it: executing all 17 against the database and counting what
came back. Three throw, four are right, ten are wrong or unscoped. Prose counts drift. A query
does not. That is the same argument `AI_NOTES.md` makes for writing evals early, and I
evidently needed to learn it twice.

**The design consequence.** Because model SQL fails in this many independent ways, I inverted
the trust relationship: the model classifies intent and surfaces entities, and the semantic
layer owns reviewed SQL per intent (`CertifiedQueries`). Model SQL is the guarded fallback for
unrecognised intents, never the primary path. Any response that ran SQL reports `sqlSource`
and, when we overrode the model, why. Clarifications and refusals never executed anything, so
neither field is present on those paths.

---

## 3. Ambiguities and the decisions I made

### 3.1 "Did Riverside beat Oak Hill?", opposite answers by sport

The best question in the set. All three meetings:

| Sport | Date | Result |
|---|---|---|
| Football | 2025-09-05 | Oak Hill 24 – Riverside 21 → **Riverside lost** |
| Basketball | 2026-01-20 | Riverside 100 – Oak Hill 89 → Riverside won |
| Basketball | 2026-02-06 | Oak Hill 72 – Riverside 79 → Riverside won |

A bare yes/no is wrong roughly half the time, and Riverside is the *away* team in two of
three, so the model's home-only join finds just one game.

**Decision:** require the `sport` slot. Return `NeedsClarification` with both sports as
options. Once filled, answer definitively. Two goldens pin both branches so the opposite
answers stay locked in.

### 3.2 "How many points did Jackson score?", three candidate entities

"Jackson" is simultaneously **Jackson Prep** (school), **Jackson, GA** (city, the city
Jackson Prep plays in), and **Tony Jackson** (Riverside RB). The model silently chose the
football team.

**Decision:** clarify with all three candidates. Getting this right needed two refinements:

- **Subsumption.** "Tony Jackson" contains "Jackson", so a question naming Tony Jackson is
  *not* ambiguous, the longer match wins. Without this every player whose surname is also a
  place became spuriously ambiguous.
- **Shadowing.** The reverse: the city "Jackson" is shadowed by two longer names, so a bare
  "Jackson" must never auto-resolve to it. My first implementation resolved silently to the
  *city*, which would have returned zero rows and looked like a real answer. I caught this
  only by reading the resolved-slots diagnostic on a passing-looking response.

### 3.3 "Who is the best player?", no defensible default

No metric exists in the data. "Most points" mixes sports (§1.6) and rewards 14-game schedules
(§1.5).

**Decision:** `NeedsClarification` on both `metric` and `sport`, offering concrete options.
Refusing outright would be defensible. Asking is better product.

### 3.4 "This season" has no referent

Football `2025` and Basketball `2025-26` coexist. I resolve sport → season deterministically
from the data (`DatasetFacts`), so I clarify **sport, not season**, one question instead of
two. If a sport ever gains a second season, `HasSingleSeasonPerSport` goes false and season
must become a slot in its own right.

### 3.5 Sport is often derivable, so don't ask

Asking "which sport?" about Marcus Bell is noise, he plays one. I derive sport from a
resolved entity when that entity implies exactly one, and only clarify when genuinely
ambiguous. My first version asked anyway; it was technically safe and felt stupid.

### 3.6 "How many teams" = 16, not 10

`teams` is one row per school per sport. 16 teams, 10 schools. I answer 16 and say so in the
scope string, because the question asked about teams.

---

## 4. A note on the harness itself

`FakeLlmClient` normalisation strips all non-alphanumerics, so `"Marcus Bell's"` becomes
`marcus bells`. That's fine for matching recorded questions, but it's worth flagging that a
naive entity matcher built on the same normalisation would fail to find "Marcus Bell" in the
possessive form. My lexicon matching runs on the raw question for this reason.

Not a bug in the fake, just a sharp edge inherited by anything reusing that normalisation.

---

## 5. Left out on purpose, and what I'd do next

The time box ran out during Part 3. Everything below is a deliberate decision, not an oversight
, each item was considered and deferred with a reason.

### Left out on purpose

| Not built | Why |
|---|---|
| **Auth / login / sessions** | The brief lists auth under "what we don't care about". What ships is *authorization over what the model may read*, which is the model-trust problem and is graded. Identity is a header. |
| **Admin dashboard UI** | No frontend exists in this submission and UI is explicitly out of scope. Two read-only ops endpoints ship instead. |
| **Live LLM (Vertex/OpenAI)** | The submission must run offline with no keys. `ILlmClient` is the seam; a real client is one class. |
| **Cloud deploy, containers** | Out of scope per the brief. Topology is documented in PRODUCTION_NOTES §5. |
| **Answering all 17 questions** | "10 questions brilliantly beats 17 blandly." 13 are answered or correctly clarified; 4 are refused by design. |
| **A real SQL parser** | The guard is a conservative prefilter and says so. A parser is the correct answer and is the top item below. |

### What I'd do next, in priority order

**1. Replace the regex guard with an AST validator.** An adversarial review produced six working
bypasses (quoted identifiers, comments-as-whitespace, a UNION leg, CTE shadowing,
`pragma_table_info`, and a recursive CTE that ran 31s against a 5s timeout). All six are closed
and pinned by tests, but they were closed by *refusing syntax*, no CTEs, no quoted identifiers,
not by understanding it. That is the right trade for a prefilter and the wrong trade forever.
Note also that `Microsoft.Data.Sqlite`'s `CommandTimeout` does not bound CPU-heavy queries, so a
cost guard has to come from the validator or a progress handler.

**2. Make the model-SQL path real and tested.** It is currently unreachable (ARCHITECTURE §1).
The honest version: certified templates only for the intents forensics proved broken, guarded
model SQL for the ones proven correct, each pinned by a golden asserting `sqlSource: Model`. That
demonstrates the hybrid rather than describing it, and calibrates trust per route by evidence
instead of applying one posture everywhere.

**3. Collapse `CapabilityRouter`.** Measurement shows `Deep` grants nothing `Aggregate` doesn't,
the tier never touches `MaxRows`, and `capability_not_granted` is unreachable. Keep the role
grant, fold in the ops flag, delete the tier classifier.

**4. Column-level authorization and alias resolution.** Role grants are table-level, and
`HasColumn` proves a column exists *somewhere* rather than on the aliased table. Both need the
parser from item 1. In Postgres this belongs in row-level security.

**5. Decide the clipped-stat policy.** A 52-way tie probably deserves `CannotAnswer` with an
explanation rather than 52 rows. That is a product call I flagged rather than made.

**6. Per-sport metric registry.** Enforce structurally that `points` cannot be summed across
sports, rather than by convention plus a caveat.

**7. Real slot confidence.** `MinSlotConfidence` is configured and threaded but resolution is
boolean. Scoring it would let near-misses clarify instead of failing.

**8. Result cache** keyed on `(normalised question, filled slots, role, schema version)`. Role
*must* be in the key or a cache hit becomes privilege escalation.

**9. Semantic-model retrieval** once it outgrows a single prompt payload.

**10. Fuzzy entity resolution**, the highest-leverage item for the real product, because the
same lexicon powers site autocomplete and chatbot slot filling. PRODUCTION_NOTES §4.

### Known limitations I am not treating as bugs

- **Goldens are broad but shallow.** 24 across 16 failure classes, most classes carrying one
  or two. Only `clarification-loop-closes` (4) and `untrusted-slot-input` (3) go deeper, and
  both earned it by being where a real bug got through. Good regression coverage, not a
  substitute for fuzzing.
- **Four goldens have no `groundTruthSql`**, `best-player-subjective`,
  `unsupported-question`, `anonymous-denied-player-stats` and `rejects-unknown-metric-slot`,
  because they assert refusal, clarification or slot-rejection behaviour rather than a value.
  Nothing in the data to derive.
- **`Verifier` asserts on the first row only**, leaning on `rowCount` and `isTie` to catch
  ordering regressions.
- **Certified templates interpolate `Metric.Expression` and column names.** Safe because
  `Metric` is a closed set, but that invariant lives in a comment rather than a type.
