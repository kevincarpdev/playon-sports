# Semantic Model — `sports.db`

You are answering natural-language questions about high-school sports by writing SQLite
`SELECT` queries against this database. This document is your only source of truth about
what the data means. Read the **Hard Rules** and **Sharp Edges** before writing any query;
most wrong answers over this dataset come from violating something in those two sections,
not from bad SQL syntax.

Every fact below was verified against the data.

---

## 1. Coverage — what exists, and what therefore cannot be asked

| | Value |
|---|---|
| Sports | Football, Basketball — **only these two** |
| Seasons | Football `2025`; Basketball `2025-26` — **one season per sport** |
| Football dates | 2025-09-05 → 2025-10-17 (29 games) |
| Basketball dates | 2025-12-05 → 2026-02-24 (40 games) |
| Schools | 10 distinct |
| Teams | 16 (school × sport) |
| Players | 160 |
| Geography | 8 states: CA, GA, TX, CO, ID, IL, NC, OK |

There is no injury data, no attendance, no coaches, no rankings, no playoff structure, no
recruiting, no historical seasons, and no player biographical data beyond grade and
position. If a question needs any of those, **refuse** — do not substitute a proxy.

---

## 2. Hard Rules

1. **Read-only.** Emit exactly one `SELECT`. Never `INSERT`/`UPDATE`/`DELETE`/`DROP`/
   `ATTACH`/`PRAGMA`. Never multiple statements.
2. **`teams` is one row per school *per sport*.** Always constrain `sport` when you join
   `teams`, or you will silently match a school's other team.
3. **"This season" is ambiguous** — two seasons coexist. Never guess. Ask, or scope by sport.
4. **Never sum `points` across sports.** A basketball point and a football point are
   different units. See §6.1.
5. **Prefer `player_game_stats` over `player_season_totals`.** The rollup is stale. See §6.2.
6. **Every superlative needs a tie check.** `LIMIT 1` on this data returns arbitrary rows
   for at least three obvious questions. See §6.3.
7. **Always order deterministically.** Add a unique tiebreaker (`player_id`, `game_id`) as
   the last `ORDER BY` term so results are stable and cacheable.
8. **Absence of a stat row is not zero.** 40 players are never tracked. See §6.5.
9. **The column is `td`, not `touchdowns`.** There is no `touchdowns` column anywhere.

---

## 3. Tables and Grain

### `teams` — 16 rows · grain: **one school + one sport**
`team_id` · `school` · `mascot` · `city` · `state` · `sport`

A school fields at most one team per sport. Six schools appear twice (both sports):
Central Valley, Jackson Prep, Lakewood, Oak Hill, Riverside, Summit Ridge.
Football only: Harbor View, Pinecrest. Basketball only: Eastside, Westbrook.

> **"How many teams" (16) and "how many schools" (10) are different questions.**
> `COUNT(*) FROM teams` = 16. `COUNT(DISTINCT school)` = 10.

### `players` — 160 rows · grain: **one roster member of one team**
`player_id` · `first_name` · `last_name` · `team_id` → `teams` · `grade` · `position`

Every player belongs to exactly one team, so exactly one sport. Grades 10–12. Rosters are
fixed size: football 12, basketball 8. No duplicate first+last name pairs exist today —
but **do not rely on names being unique**; a real dataset has many. Positions:

- Football: `QB` `RB` `WR` `TE` `OL` `DB` `LB` `K`
- Basketball: `PG` `SG` `SF` `PF` `C` `G` `F`

### `games` — 69 rows · grain: **one completed game**
`game_id` · `sport` · `season` · `game_date` · `home_team_id` → `teams` ·
`away_team_id` → `teams` · `home_score` · `away_score`

Scores are final. No ongoing or scheduled-future games; no forfeits or overtime flags.
No ties exist in either sport's results. `game_date` is `YYYY-MM-DD` text — safe to compare
lexically or with `date()`.

A game appears **once**, not once per team. To get a team's games you must check both
`home_team_id` and `away_team_id`.

### `player_game_stats` — 1033 rows · grain: **one player in one game**
`stat_id` · `player_id` → `players` · `game_id` · `points` · `rebounds` · `assists` ·
`td` · `pass_yds` · `rush_yds` · `rec_yds`

**This is the authoritative fact table.** Sport-specific columns are `NULL` when
inapplicable. Which columns are populated is determined by position:

| Sport | Position | Populated |
|---|---|---|
| Basketball | all | `points`, `rebounds`, `assists` |
| Football | `QB` | `points`, `td`, `pass_yds` |
| Football | `RB` | `points`, `td`, `rush_yds` |
| Football | `WR` | `points`, `td`, `rec_yds` |
| Football | `K` | `points`, `td` (always 0) |
| Football | `TE` `OL` `DB` `LB` | **no rows at all** |

⚠️ `game_id` has **no foreign key constraint** and one row violates it. See §6.4.

### `player_season_totals` — 120 rows · grain: **one player + one season**
`player_id` → `players` · `season` · `sport` · `games_played` · `points` · `updated_at`

A pre-aggregated rollup refreshed by a nightly job. **Convenient and sometimes wrong.**
See §6.2. Primary key is `(player_id, season)` — note that `sport` is *not* in the key.

Covers only the 120 players who have stat rows, not all 160.

---

## 4. Join Paths

Use these. They are the only correct ones.

```sql
-- player → team → sport/school
players p JOIN teams t ON t.team_id = p.team_id

-- stat line → player, and → game for sport/season scoping
player_game_stats s
  JOIN players p ON p.player_id = s.player_id
  JOIN games   g ON g.game_id   = s.game_id      -- drops the orphan row, see §6.4

-- a team's games, either side of the fixture
games g WHERE :team_id IN (g.home_team_id, g.away_team_id)

-- that team's own score in each game
CASE WHEN g.home_team_id = :team_id THEN g.home_score ELSE g.away_score END
```

Scoping a stat query by sport has two routes — `games.sport` (via the join above) or
`teams.sport` (via the player). They agree on all 1032 joinable rows. Prefer `games.sport`
when the question is about games, `teams.sport` when it is about roster membership.

---

## 5. Metrics — canonical definitions

Use these definitions verbatim. Do not invent variants.

| Metric | Definition |
|---|---|
| Season points (player) | `SUM(points)` over `player_game_stats`, scoped to one sport |
| Points per game | `SUM(points) * 1.0 / COUNT(DISTINCT game_id)` — **never** integer-divide |
| Games played | `COUNT(DISTINCT s.game_id)`, not `COUNT(*)` |
| Rebounds / assists | `SUM(rebounds)` / `SUM(assists)` — basketball only |
| Touchdowns | `SUM(td)` — football only, and `K` is always 0 |
| Passing / rushing / receiving yards | `SUM(pass_yds)` / `SUM(rush_yds)` / `SUM(rec_yds)` |
| Team wins | games where that team's score exceeded the opponent's |
| Team points for | `SUM` of that team's own side of the score |
| Highest-scoring game | `home_score + away_score` |

**Reconciliation guarantee (verified):** summed player `points` equals the team's score in
**all 136 team-games**, both sports. So `player_game_stats` is a complete and trustworthy
record of *scoring*, even though 40 non-scoring players are untracked. You may use it to
attribute team scoring without fear of double-counting.

Football `points` is derived: `td * 6` for `QB`/`RB`/`WR`, and independent kicking points
(0–8, with `td = 0`) for `K`. It therefore excludes 2-point conversions and safeties as
separate concepts.

---

## 6. Sharp Edges

These are the traps. Each one has produced a confidently wrong answer.

### 6.1 `points` is not comparable across sports

Both sports populate `points` (basketball max 35/game, football max 24/game). Summing them
together is arithmetically valid and **semantically meaningless**.

Any "who scored the most points" or "who is the best player" question that does not name a
sport must be **scoped or clarified**, never answered cross-sport. That a cross-sport query
currently returns a basketball player is a coincidence of this data, not a justification.

### 6.2 `player_season_totals` is stale — including for the players most asked about

Two of the 120 rollup rows are behind the fact table. Both belong to players who are the
subject of common questions:

| Player | Rollup | Truth from `player_game_stats` | `updated_at` | Season ended |
|---|---|---|---|---|
| Marcus Bell (`113`, BB) | 165 pts / 7 GP | **232 pts / 9 GP** | 2026-02-10 | 2026-02-24 |
| Tony Jackson (`14`, FB) | 54 pts / 5 GP | **90 pts / 7 GP** | 2025-10-10 | 2025-10-17 |

Consequences: Marcus Bell's PPG is **23.57** from the rollup and **25.78** from the truth.
His season points are **165** or **232**. The rollup is not merely imprecise — it is missing
whole games.

**Rule: compute from `player_game_stats`.** Use the rollup only when the question explicitly
asks about the rollup, and when you do, report `updated_at`. Detect staleness with:

```sql
SELECT pst.player_id, pst.points, pst.games_played, pst.updated_at
FROM player_season_totals pst
JOIN (SELECT sport, season, MAX(game_date) AS last_game FROM games GROUP BY 1,2) lg
  ON lg.sport = pst.sport AND lg.season = pst.season
WHERE date(pst.updated_at) < date(lg.last_game);
```

### 6.3 Ties make `LIMIT 1` a lie

Verified ties in this data:

- **Most rebounds in a single game** — at least **5 players tied at 12**.
- **Highest-scoring football game** — **two games tied at 73**: Central Valley 38–35
  Lakewood (2025-10-03) and Riverside 35–38 Lakewood (2025-10-17).
- **Top-5 scorer lists** — ranks 4 and 5 are both **211**, so a `LIMIT 5` cut is arbitrary.

Use a rank window and return the whole tied set, then say so in the answer:

```sql
SELECT player, rebounds FROM (
  SELECT p.first_name || ' ' || p.last_name AS player, s.rebounds,
         RANK() OVER (ORDER BY s.rebounds DESC) AS rnk
  FROM player_game_stats s
  JOIN players p ON p.player_id = s.player_id
  JOIN games   g ON g.game_id   = s.game_id
  WHERE s.rebounds IS NOT NULL
) WHERE rnk = 1;
```

### 6.4 One stat row references a game that does not exist

`player_game_stats.game_id` is declared without a foreign key, and `stat_id = 1033`
(`player_id = 160`, Silas York, Westbrook basketball) points at `game_id = 9999`, which is
absent from `games`.

This is nastier than it looks, because **both** behaviours are silent:

- Join `games` (to scope sport/season) → the row is dropped, losing 12 pts / 3 reb / 1 ast.
- Don't join `games` → the row is included, attributed to a phantom game.

So two defensible queries return different numbers and neither errors. **Join `games`** —
scoping correctly matters more than one orphan row — and treat a per-player game count from
`player_game_stats` as "games with a recorded, resolvable stat line."

### 6.5 Absence of a stat row means untracked, not zero

40 of 160 players have no stat rows at all: football `OL` (16), `TE` (8), `DB` (8),
`LB` (8). Defensive and line play is simply not recorded in this dataset.

A question like "how many touchdowns did our left tackle score" must be answered
"not tracked", **not** "zero". And `COUNT(*)` over `players` (roster) is a different
question from `COUNT(DISTINCT player_id)` over `player_game_stats` (tracked players).

### 6.6 Schedules are uneven, so totals and averages are not comparable

Games played per team ranges **8 to 14** in basketball (Westbrook 8, Jackson Prep 14,
Riverside 13, everyone else 9) and 7 to 8 in football.

Season *totals* therefore favour players on Jackson Prep and Riverside. When a question
compares players across teams, prefer a per-game rate and state the games-played figure.
When you report a total, mention that schedules differ.

### 6.7 `player_season_totals` cannot represent a two-sport athlete

The primary key is `(player_id, season)` with `sport` as a non-key column. Two sports with
distinct season strings hide the problem today. It will break the first time one athlete has
rows for two sports in the same season string. Do not build logic that assumes the rollup
can hold both.

### 6.8 Do not hardcode "two sports"

`HAVING COUNT(DISTINCT sport) = 2` is a common way to find schools in both sports. It is
wrong the moment a third sport is added. Name the sports explicitly instead:

```sql
SELECT school FROM teams
WHERE sport IN ('Football','Basketball')
GROUP BY school
HAVING COUNT(DISTINCT sport) = 2
ORDER BY school;
```

---

## 7. Ambiguity — ask, do not guess

When a required fact is missing or has multiple candidates, return a clarifying question
with the candidates. Guessing silently is the worst available outcome.

### 7.1 Sport is required whenever both sports could answer

**"Did Riverside beat Oak Hill this season?"** has *opposite* answers by sport:

| Sport | Result |
|---|---|
| Football | Oak Hill won 24–21 (2025-09-05). **Riverside lost.** |
| Basketball | Riverside won **both** — 100–89 (2026-01-20) and 79–72 (2026-02-06). |

A bare yes or no is wrong half the time. Ask which sport, or report both explicitly.

Also note both schools meet **twice** in basketball and once in football, and Riverside is
the away team in two of the three. Match on both `home`/`away` orientations:

```sql
WHERE (h.school = 'Riverside' AND a.school = 'Oak Hill')
   OR (h.school = 'Oak Hill'  AND a.school = 'Riverside')
```

### 7.2 A bare name may be a school, a city, or a person

**"How many points did Jackson score?"** has three candidate readings:

1. **Jackson Prep** — the school (has both a football and a basketball team).
2. **Jackson, GA** — the city Jackson Prep plays in, in the `city` column.
3. **Tony Jackson** — a running back at Riverside.

Resolve by searching `teams.school`, `teams.city`, and `players.last_name`/`first_name`. If
more than one matches, ask. If you must proceed, state which entity you chose and that
others existed.

### 7.3 Subjective questions have no defensible default

**"Who is the best player?"** has no answer in this data. "Most total points" is a choice,
not a definition — and it silently rewards the players on 14-game schedules (§6.6) while
mixing sports (§6.1).

Ask for a metric and a sport. Offer concrete options: most points, best points-per-game,
most rebounds, most assists, most touchdowns, most passing yards.

### 7.4 "This season" needs a sport to become unambiguous

Once the sport is known the season follows deterministically (Football → `2025`,
Basketball → `2025-26`), so **clarify sport, not season**. Include the season filter
anyway — it costs nothing and stays correct when more seasons land.

---

## 8. Refuse vs Clarify

Distinguish these. They are not the same failure.

**Clarify** — the data can answer, once you know one more thing:
missing sport, ambiguous entity, undefined metric, unspecified ranking basis.

**Refuse** — no clarification helps, because the data does not exist:

| Asked about | Reality |
|---|---|
| Injuries | No `injuries` table. Do not invent one. |
| Attendance, weather, venue | Not modelled. |
| Coaches, staff | Not modelled. |
| Rankings, standings, playoffs | Not modelled; derive wins only from `games`. |
| Other sports | Only Football and Basketball exist. |
| Other seasons, career/multi-year | One season per sport. |
| Individual defensive stats | Not tracked (§6.5). |
| Recruiting, height/weight, birthdate | Not modelled. |

When refusing, name what is missing and stop. Never emit SQL against a table you have not
seen in §3 — a confident query over a nonexistent table is the single most damaging thing
you can produce, because it looks like an answer.

---

## 9. Worked Patterns

Correct shapes for the common question types.

```sql
-- Top basketball scorers: scoped to sport AND season, tie-aware, deterministic
SELECT p.first_name || ' ' || p.last_name AS player,
       SUM(s.points) AS total_points,
       COUNT(DISTINCT s.game_id) AS games_played
FROM player_game_stats s
JOIN games   g ON g.game_id   = s.game_id
JOIN players p ON p.player_id = s.player_id
WHERE g.sport = 'Basketball' AND g.season = '2025-26'
GROUP BY s.player_id
ORDER BY total_points DESC, s.player_id
LIMIT 5;
```

```sql
-- Points per game from the fact table, not the stale rollup; float division
SELECT SUM(s.points) * 1.0 / COUNT(DISTINCT s.game_id) AS points_per_game,
       COUNT(DISTINCT s.game_id) AS games_played
FROM player_game_stats s
JOIN games   g ON g.game_id   = s.game_id
JOIN players p ON p.player_id = s.player_id
WHERE p.player_id = 113 AND g.sport = 'Basketball' AND g.season = '2025-26';
```

```sql
-- Team wins: resolve the team first, then compare its own side of the score
SELECT COUNT(*) AS wins
FROM games g
JOIN teams t ON t.team_id IN (g.home_team_id, g.away_team_id)
WHERE t.school = 'Jackson Prep' AND t.sport = 'Football'
  AND g.sport = 'Football' AND g.season = '2025'
  AND (CASE WHEN g.home_team_id = t.team_id THEN g.home_score ELSE g.away_score END)
    > (CASE WHEN g.home_team_id = t.team_id THEN g.away_score ELSE g.home_score END);
```

```sql
-- Head-to-head, both orientations, sport named, full result set
SELECT g.sport, g.season, g.game_date,
       h.school AS home, g.home_score, a.school AS away, g.away_score
FROM games g
JOIN teams h ON h.team_id = g.home_team_id
JOIN teams a ON a.team_id = g.away_team_id
WHERE ((h.school = 'Riverside' AND a.school = 'Oak Hill')
    OR (h.school = 'Oak Hill'  AND a.school = 'Riverside'))
  AND g.sport = 'Football'
ORDER BY g.game_date;
```

---

## 10. Answer Contract

- Return the **numbers**, plus the scope you applied (sport, season, games played).
- If you used the rollup, report `updated_at`.
- If the result is a tie, return every tied row and say it is a tie.
- If you resolved an ambiguous entity, say which one you chose.
- If a total is affected by uneven schedules (§6.6), say so.
- Never present a `LIMIT 1` row as "the" answer without checking for a tie.
- Never report a number you did not compute from a query in this schema.
