# Findings

What I learned about this dataset and this model. Everything here was verified with my own
queries; the SQL to re-derive each claim is in `src/SportsQa.EvalRunner/goldens.json` under
`groundTruthSql`.

Where a finding has a direct analogue in MaxPreps production data, I've noted it — the
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
coincidence — the rollup is *convenient*, the model reaches for it explicitly "for
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

### 1.3 `rebounds` and `assists` are clipped — I don't think this one was planted

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

I return the full tied set with a `tied_result` caveat. Arguably it should refuse outright;
I documented that judgement call rather than silently picking.

### 1.4 40 of 160 players have no stat rows at all — and it's structural

The untracked players are entirely football `OL` (16), `TE` (8), `DB` (8), `LB` (8). Every
offensive lineman and every defensive player. Only `QB`, `RB`, `WR` and `K` are tracked.

So absence of a stat line means **untracked**, not zero. "How many touchdowns did our left
tackle score?" must answer "not tracked". And roster size (12) is a different question from
tracked players (4 per football team).

This one is real: MaxPreps genuinely does not carry individual OL statistics.

### 1.5 Schedules are badly uneven, which distorts every season total

Games played per team: basketball ranges **8 to 14** (Westbrook 8, Jackson Prep 14, Riverside
13, everyone else 9); football 7 to 8.

Marcus Bell leads basketball scoring with 232 points in **9** games while Eli Quigley has 222
in **14**. On a per-game basis Bell (25.8) is far ahead of Quigley (15.9), but a naive
"total points" leaderboard makes them look close. Any cross-team total comparison needs the
games-played denominator attached, which is why `uneven_schedules` is a caveat.

### 1.6 A positive finding: player points reconcile exactly to team scores

Worth stating because it licenses a whole class of queries. Summed player `points` equals the
team's own score in **all 136 team-games**, both sports, zero mismatches. Despite 40
untracked players, scoring attribution is complete and non-double-counted.

Football `points` is derived: `td * 6` for QB/RB/WR, and independent kicking points (0–8,
`td` always 0) for K. So `SUM(td)` and `SUM(points)` measure different things, and a kicker
has points with zero touchdowns.

### 1.7 `player_season_totals` cannot represent a two-sport athlete

Primary key is `(player_id, season)` with `sport` as a *non-key* column. Two sports with
distinct season strings ('2025', '2025-26') hide the flaw. It breaks the first time one
athlete has rows for two sports under the same season string — which is routine for real
high-school athletes.

---

## 2. Model interpretations I distrust

Five of the 17 recorded interpretations are broken, in five different ways. Reported model
confidence ranges 0.88–0.95 across them, which is the whole lesson: **self-reported
confidence is uncorrelated with correctness and cannot be the gate.**

| Question | Model confidence | Defect |
|---|---|---|
| Tony Jackson touchdowns | 0.91 | `SUM(s.touchdowns)` — **column does not exist** (it's `td`). Throws. |
| Riverside injuries | 0.88 | `FROM injuries` — **table does not exist**. Confident SQL over nothing. |
| Did Riverside beat Oak Hill | 0.90 | `'Riverside AND a.school = '` — **unbalanced quote**, syntax error. |
| Did Riverside beat Oak Hill | 0.90 | Also checks **Riverside as home only** — misses 2 of 3 meetings. |
| Who is the best player | 0.95 | Asserts "best = most total points". Subjective, and cross-sport. |

Plus three that execute cleanly and are still wrong or misleading:

**Marcus Bell's points and PPG (0.93–0.94)** read the stale rollup, "using the pre-aggregated
season totals table for efficiency". Returns 165 and 23.57 instead of 232 and 25.78. This is
the most dangerous category: plausible SQL, plausible number, no error, wrong answer.

**Three superlatives use `LIMIT 1` over tied data.** Most rebounds (52-way tie), highest
scoring football game (2 games tied at 73 — Central Valley 38–35 Lakewood and Riverside 35–38
Lakewood). The model returns one arbitrary row as fact. Its highest-scoring-game query also
returns `game_id` with **no team names**, which is useless to a fan even when correct.

**"Who scored the most points this season" (0.92)** sums `points` across both sports. That it
currently returns a basketball player is luck, not correctness.

Two more latent bugs that happen to be harmless *today*:

- `top5_scorers_basketball` omits the season filter. Fine with one basketball season; wrong the
  day a second lands.
- `schools_both_sports` uses `HAVING COUNT(DISTINCT sport) = 2`. Breaks on a third sport.

**The design consequence.** Because model SQL fails in this many independent ways, I inverted
the trust relationship: the model classifies intent and surfaces entities, and the semantic
layer owns reviewed SQL per intent (`CertifiedQueries`). Model SQL is the guarded fallback for
unrecognised intents, never the primary path. Every response reports `sqlSource` and, when we
overrode the model, why.

---

## 3. Ambiguities and the decisions I made

### 3.1 "Did Riverside beat Oak Hill?" — opposite answers by sport

The best question in the set. All three meetings:

| Sport | Date | Result |
|---|---|---|
| Football | 2025-09-05 | Oak Hill 24 – Riverside 21 → **Riverside lost** |
| Basketball | 2026-01-20 | Riverside 100 – Oak Hill 89 → Riverside won |
| Basketball | 2026-02-06 | Oak Hill 72 – Riverside 79 → Riverside won |

A bare yes/no is wrong roughly half the time, and Riverside is the *away* team in two of
three, so the model's home-only join finds just one game.

**Decision:** require the `sport` slot. Return `needs_clarification` with both sports as
options; once filled, answer definitively. Two goldens pin both branches so the opposite
answers stay locked in.

### 3.2 "How many points did Jackson score?" — three candidate entities

"Jackson" is simultaneously **Jackson Prep** (school), **Jackson, GA** (city — the city
Jackson Prep plays in), and **Tony Jackson** (Riverside RB). The model silently chose the
football team.

**Decision:** clarify with all three candidates. Getting this right needed two refinements:

- **Subsumption.** "Tony Jackson" contains "Jackson", so a question naming Tony Jackson is
  *not* ambiguous — the longer match wins. Without this every player whose surname is also a
  place became spuriously ambiguous.
- **Shadowing.** The reverse: the city "Jackson" is shadowed by two longer names, so a bare
  "Jackson" must never auto-resolve to it. My first implementation resolved silently to the
  *city*, which would have returned zero rows and looked like a real answer. I caught this
  only by reading the resolved-slots diagnostic on a passing-looking response.

### 3.3 "Who is the best player?" — no defensible default

No metric exists in the data. "Most points" mixes sports (§1.6) and rewards 14-game schedules
(§1.5).

**Decision:** `needs_clarification` on both `metric` and `sport`, offering concrete options.
Refusing outright would be defensible; asking is better product.

### 3.4 "This season" has no referent

Football `2025` and Basketball `2025-26` coexist. I resolve sport → season deterministically
from the data (`DatasetFacts`), so I clarify **sport, not season** — one question instead of
two. If a sport ever gains a second season, `HasSingleSeasonPerSport` goes false and season
must become a slot in its own right.

### 3.5 Sport is often derivable, so don't ask

Asking "which sport?" about Marcus Bell is noise — he plays one. I derive sport from a
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

Not a bug in the fake — just a sharp edge inherited by anything reusing that normalisation.

---

## 5. What I'd do next (the time-box cut line)

Ordered by value, not effort. The box ran out at Part 3; everything below is deliberate debt.

**Correctness**

1. **Push tie-awareness into SQL universally.** `DENSE_RANK()` is in the certified templates
   but tie detection still inspects results afterward. A single ranked-query builder should
   make it structural.
2. **Decide the clipped-stat policy (§1.3).** A 52-way tie probably deserves
   `cannot_answer` with an explanation, not a 52-row answer. Needs a product call.
3. **Metric direction.** `Metric` assumes higher-is-better. Track & field breaks this on day
   one (times are lower-is-better) — see [PRODUCTION_NOTES.md](PRODUCTION_NOTES.md) §3.
4. **Per-sport metric registry.** Enforce structurally that `points` can never be summed
   across sports, rather than by convention plus a caveat.

**Robustness**

5. **Unit tests on `SqlGuard`.** It is the security boundary and currently has only
   end-to-end coverage. It deserves adversarial cases: comment injection (`--`, `/* */`),
   `UNION` reaching an unauthorised table, CTE-shadowed table names, nested subqueries.
6. **Real slot confidence.** `MinSlotConfidence` is configured and threaded but resolution is
   currently boolean. Scoring resolution quality would let near-misses clarify instead of
   failing.
7. **Column-level authorization.** Role grants are table-level. A real tier boundary runs
   through columns, and in Postgres belongs in row-level security.

**Scale and cost**

8. **Result cache** keyed on `(normalised question, filled slots, role, schema version)`.
   Role *must* be in the key or a cache hit becomes privilege escalation.
9. **Semantic-model retrieval.** It is one prompt payload today. Past a few sports it needs
   chunking and per-question retrieval.
10. **Fuzzy entity resolution** — the highest-leverage item for the real product, because the
    same lexicon powers both site autocomplete and chatbot slot filling. See
    [PRODUCTION_NOTES.md](PRODUCTION_NOTES.md) §4.
