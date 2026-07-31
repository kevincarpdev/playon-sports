# Dataset: `sports.db`

A small slice of a high-school sports statistics platform. Two sports (Football, season `2025`;
Basketball, season `2025-26`). SQLite, no auth, read-only for your purposes.

| Table | Rows | Notes |
|---|---|---|
| `teams` | 16 | One row per school **per sport** (a school can appear twice). |
| `players` | 160 | Roster members, FK to `teams`. |
| `games` | 69 | One row per game; `home_score`/`away_score` are final. |
| `player_game_stats` | 1033 | Per-player, **per-game** stat lines. Sport-specific columns are NULL when not applicable (e.g. `rebounds` for football players). |
| `player_season_totals` | 120 | Pre-aggregated season rollups, refreshed by a nightly job (`updated_at`). |

Columns of note in `player_game_stats`: `points`, `rebounds`, `assists`, `td`, `pass_yds`,
`rush_yds`, `rec_yds`.

This is production-shaped data. Like all production data, its quality is your problem, not a
given. Anything you conclude about it belongs in `FINDINGS.md`.
