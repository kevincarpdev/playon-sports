#!/usr/bin/env bash
# Exercises every /ask outcome against a running API and checks the HTTP status and key
# values. Complements the eval harness: this proves the HTTP surface, the evals prove the
# answers. Start the API first (see README), then run this.
set -uo pipefail

BASE="${BASE:-http://localhost:5000}"
PASS=0
FAIL=0

# check <name> <expected-status> <expected-substring> <json-body> [role]
check() {
  local name="$1" want_status="$2" want_body="$3" payload="$4" role="${5:-}"
  local args=(-s -o /tmp/smoke.out -w '%{http_code}' -X POST "$BASE/ask"
              -H 'Content-Type: application/json')
  [[ -n "$role" ]] && args+=(-H "X-SportsQa-Role: $role")
  args+=(-d "$payload")

  local status; status=$(curl "${args[@]}")
  local body; body=$(cat /tmp/smoke.out)

  if [[ "$status" == "$want_status" ]] && grep -q "$want_body" <<<"$body"; then
    printf '  \033[32mPASS\033[0m  %-46s %s\n' "$name" "$status"
    PASS=$((PASS + 1))
  else
    printf '  \033[31mFAIL\033[0m  %-46s got %s want %s\n' "$name" "$status" "$want_status"
    printf '        looking for: %s\n        body: %.240s\n' "$want_body" "$body"
    FAIL=$((FAIL + 1))
  fi
}

get() {
  local name="$1" want_status="$2" path="$3" role="${4:-}"
  local args=(-s -o /tmp/smoke.out -w '%{http_code}' "$BASE$path")
  [[ -n "$role" ]] && args+=(-H "X-SportsQa-Role: $role")

  local status; status=$(curl "${args[@]}")
  if [[ "$status" == "$want_status" ]]; then
    printf '  \033[32mPASS\033[0m  %-46s %s\n' "$name" "$status"
    PASS=$((PASS + 1))
  else
    printf '  \033[31mFAIL\033[0m  %-46s got %s want %s\n' "$name" "$status" "$want_status"
    FAIL=$((FAIL + 1))
  fi
}

if ! curl -sf "$BASE/health" >/dev/null; then
  echo "API is not responding at $BASE — start it first:"
  echo "  cd src/SportsQa.Api && dotnet run"
  exit 2
fi

echo
echo "Smoke testing $BASE"
echo "=============================================================================="

echo "Answered"
get   "health reports ok"                        200 "/health"
check "team count is 16"                         200 '"scalar":16' '{"question":"How many teams are in the database?"}'
check "hallucinated column still answers 15"     200 '15'          '{"question":"How many touchdowns did Tony Jackson score this season?"}'
check "stale rollup bypassed (232 not 165)"      200 '232'         '{"question":"How many total points has Marcus Bell scored this season?"}'
check "passing yards 2047"                       200 '2047'        '{"question":"What is Derek Foss'"'"'s total passing yards?"}'
check "tie reported, not one arbitrary row"      200 'tied_result' '{"question":"What was the highest scoring football game?"}'

echo
echo "Needs clarification"
check "ambiguous sport asks which"               200 '"slot":"sport"'  '{"question":"Did Riverside beat Oak Hill this season?"}'
check "subjective asks for a metric"             200 '"slot":"metric"' '{"question":"Who is the best player?"}'
check "ambiguous entity asks which"              200 '"slot":"entity"' '{"question":"How many points did Jackson score this season?"}'

echo
echo "Clarification loop closes"
check "football: Oak Hill won"                   200 'Oak Hill'  '{"question":"Did Riverside beat Oak Hill this season?","slots":{"sport":"Football"}}'
check "basketball: Riverside won"                200 'Riverside' '{"question":"Did Riverside beat Oak Hill this season?","slots":{"sport":"Basketball"}}'
check "resolved entity gives 139"                200 '139'       '{"question":"How many points did Jackson score this season?","slots":{"entity":"Jackson Prep","sport":"Football"}}'

echo
echo "Cannot answer"
check "nonexistent table refused"                422 'not_in_dataset'      '{"question":"How many injuries were reported for Riverside this season?"}'
check "out of scope refused"                      422 'unsupported_question' '{"question":"Who won the state championship?"}'

echo
echo "Bad request"
check "missing question is 400"                  400 'question' '{"nope":1}'
check "blank question is 400"                    400 'question' '{"question":"   "}'

echo
echo "Authorization"
check "anonymous denied per-game stats"          422 'table_not_permitted' '{"question":"How many touchdowns did Tony Jackson score this season?"}' Anonymous
check "anonymous allowed team count"             200 '"scalar":16'         '{"question":"How many teams are in the database?"}' Anonymous
get   "admin ops surface visible to admin"       200 "/admin/rollup-freshness" Admin
get   "admin ops surface hidden otherwise"       404 "/admin/rollup-freshness"

echo "=============================================================================="
echo "$PASS passed, $FAIL failed"
echo
[[ "$FAIL" -eq 0 ]]
