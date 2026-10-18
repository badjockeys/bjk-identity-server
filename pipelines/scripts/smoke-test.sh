#!/usr/bin/env bash
# End-to-end smoke test: starts the identity server against a real SQL Server, exercises the
# User module through its HTTP API and the token endpoint, checks the rows in the database,
# then restarts the app to prove startup migrations keep existing data.
#
# Expects the app to be built (Release) and these environment variables:
#   ConnectionStrings__IdentityDatabase  connection string the app uses
#   SQL_CONTAINER, SQL_PASSWORD          docker container running SQL Server, and its sa password
#   ASPNETCORE_ENVIRONMENT=Development   (the local test clients only exist in Development)
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5000}
APP_LOG=${APP_LOG:-app.log}
PROJECT=src/BJK.Idsrv.Host
: "${SQL_CONTAINER:?}" "${SQL_PASSWORD:?}"

EMAIL1=rider.one@example.com
EMAIL2=rider.two@example.com
PASSWORD='Passw0rd!'
NEW_PASSWORD='N3wPassw0rd!'

# Cloudflare's dummy Turnstile token. CI configures Cloudflare's "always passes" test secret, which accepts any
# token, so the rejection path is tested at the end with the "always fails" test secret.
DUMMY_CAPTCHA='XXXX.DUMMY.TOKEN.XXXX'
CAPTCHA=$DUMMY_CAPTCHA   # sent as X-Captcha-Token on every request; set to "" to leave it out

APP_PID=""
PASSED=()
: > "$APP_LOG"

# ---------- helpers ----------

fail() { echo "::error::$1"; exit 1; }

on_exit() {
  local status=$?
  if [ -n "$APP_PID" ]; then kill "$APP_PID" 2>/dev/null || true; fi
  if [ $status -ne 0 ]; then
    echo "::error title=App log (last 40 lines)::$(tail -40 "$APP_LOG" | sed ':a;N;$!ba;s/%/%25/g;s/\r//g;s/\n/%0A/g')"
  fi
}
trap on_exit EXIT

# start_app [VAR=value ...]  - extra settings apply to this run of the app only
start_app() {
  env "$@" dotnet run --project "$PROJECT" --configuration Release --no-build --no-launch-profile >> "$APP_LOG" 2>&1 &
  APP_PID=$!
  for _ in $(seq 1 60); do
    if curl -fs "$BASE_URL/health" > /dev/null 2>&1; then echo "ok  app is healthy"; return; fi
    kill -0 "$APP_PID" 2>/dev/null || fail "The app exited during startup."
    sleep 2
  done
  fail "The app didn't become healthy within 2 minutes."
}

stop_app() {
  kill "$APP_PID"
  wait "$APP_PID" 2>/dev/null || true
  APP_PID=""
}

# request METHOD PATH [JSON_BODY] [BEARER_TOKEN]  -> sets STATUS and RESPONSE
request() {
  local method=$1 path=$2 body=${3:-} token=${4:-}
  local args=(-s -o response.json -w '%{http_code}' -X "$method" "$BASE_URL$path")
  if [ -n "$body" ]; then args+=(-H 'Content-Type: application/json' -d "$body"); fi
  if [ -n "$token" ]; then args+=(-H "Authorization: Bearer $token"); fi
  if [ -n "$CAPTCHA" ]; then args+=(-H "X-Captcha-Token: $CAPTCHA"); fi
  STATUS=$(curl "${args[@]}")
  RESPONSE=$(cat response.json)
}

# user_token EMAIL PASSWORD  -> sets STATUS and RESPONSE (password grant, local test client)
user_token() {
  STATUS=$(curl -s -o response.json -w '%{http_code}' -X POST "$BASE_URL/connect/token" \
    -d grant_type=password -d client_id=bjk-local-user -d client_secret=local-user-secret \
    --data-urlencode "scope=openid profile email bjk.users" \
    --data-urlencode "username=$1" --data-urlencode "password=$2")
  RESPONSE=$(cat response.json)
}

admin_token() {
  STATUS=$(curl -s -o response.json -w '%{http_code}' -X POST "$BASE_URL/connect/token" \
    -d grant_type=client_credentials -d client_id=bjk-local-admin -d client_secret=local-admin-secret \
    --data-urlencode "scope=bjk.users bjk.users.manage")
  RESPONSE=$(cat response.json)
}

expect() {
  local expected=$1 label=$2
  [ "$STATUS" = "$expected" ] || fail "$label: expected HTTP $expected, got $STATUS. Response: $RESPONSE"
  echo "ok  $label ($STATUS)"
  PASSED+=("$label ($STATUS)")
}

json() { jq -r "$1" <<< "$RESPONSE"; }

body() { jq -cn "$@"; }

sql() {
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PASSWORD" -C \
    -d idsrv -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed '/^$/d'
}

expect_sql() {
  local query=$1 expected=$2 label=$3 actual
  actual=$(sql "$query" | head -1)
  [ "$actual" = "$expected" ] || fail "$label: expected '$expected', got '$actual'"
  echo "ok  $label ($actual)"
  PASSED+=("$label ($actual)")
}

# ---------- first start: creates the database ----------

start_app
grep -q "applying migrations" "$APP_LOG" || fail "Expected the first start to apply migrations."
expect_sql "SELECT COUNT(*) FROM sys.schemas WHERE name IN ('auth', 'idsrv', 'users')" 3 "auth, idsrv and users schemas exist"

request GET /.well-known/openid-configuration
expect 200 "discovery document"

# ---------- CAPTCHA ----------

CAPTCHA=""
request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p, displayName: "Bot"}')"
expect 400 "register without a CAPTCHA token is rejected"
request POST /api/users/password-reset "$(body --arg e "$EMAIL1" '{email: $e}')"
expect 400 "forgot password without a CAPTCHA token is rejected"

expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 0 "requests without a CAPTCHA created no account"

CAPTCHA=$DUMMY_CAPTCHA

# ---------- register ----------

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p, displayName: "Road Hog"}')"
expect 201 "register user 1"
USER1_ID=$(json .id)

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p, displayName: "Copycat"}')"
expect 400 "duplicate email is rejected"

request POST /api/users "$(body '{email: "weak@example.com", password: "short", displayName: "Weak"}')"
expect 400 "weak password is rejected"
[ "$(json '.errors.Password | length')" -gt 0 ] || fail "Expected password errors, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'weak@example.com'" 0 "rejected registration left no account behind"

request POST /api/users "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" '{email: $e, password: $p, displayName: "Kerb Crawler"}')"
expect 201 "register user 2"
USER2_ID=$(json .id)

# ---------- sign in and self-service ----------

user_token "$EMAIL1" "$PASSWORD"
expect 200 "user 1 signs in"
TOKEN1=$(json .access_token)

request GET /api/users/me
expect 401 "no token is unauthorised"

request GET /api/users/me "" "$TOKEN1"
expect 200 "user 1 reads own profile"
[ "$(json .email)" = "$EMAIL1" ] || fail "Wrong profile returned: $RESPONSE"

request PUT "/api/users/$USER1_ID" "$(body '{displayName: "Lane Hogger"}')" "$TOKEN1"
expect 204 "user 1 updates own display name"
expect_sql "SELECT DisplayName FROM users.Users WHERE Id = '$USER1_ID'" "Lane Hogger" "display name saved"

request GET "/api/users/$USER2_ID" "" "$TOKEN1"
expect 403 "user 1 can't read user 2"

request GET /api/users "" "$TOKEN1"
expect 403 "user 1 can't list users"

# ---------- admin ----------

admin_token
expect 200 "admin client gets a token"
ADMIN=$(json .access_token)

request GET "/api/users?page=1&pageSize=20" "" "$ADMIN"
expect 200 "admin lists users"
[ "$(json .totalCount)" = "2" ] || fail "Expected 2 users, got: $RESPONSE"

# ---------- forgot password ----------

request POST /api/users/password-reset "$(body '{email: "nobody@example.com"}')"
expect 202 "reset for unknown email looks the same"
grep -q "To: nobody@example.com" "$APP_LOG" && fail "An email was sent for an unregistered address."
echo "ok  no email sent for unknown address"

request POST /api/users/password-reset "$(body --arg e "$EMAIL1" '{email: $e}')"
expect 202 "user 1 requests a password reset"
sleep 1
# In Development the placeholder email sender writes the whole email to the log (one line in CI).
# (Only one reset email is sent in this test, so the last code in the log is user 1's.)
RESET_TOKEN=$(grep -o "Password reset code: [^ ]*" "$APP_LOG" | tail -1 | sed 's/^Password reset code: //')
[ -n "$RESET_TOKEN" ] || fail "No password reset email found in the app log."
grep -q "To: $EMAIL1" "$APP_LOG" || fail "The reset email wasn't addressed to user 1."
grep -q "reset-password?email=rider.one%40example.com&token=" "$APP_LOG" || fail "The reset email has no link to the reset page."

request POST /api/users/password-reset/confirm "$(body --arg e "$EMAIL1" '{email: $e, token: "not-a-real-token", newPassword: "N3wPassw0rd!"}')"
expect 400 "bad reset token is rejected"

request POST /api/users/password-reset/confirm "$(body --arg e "$EMAIL1" --arg t "$RESET_TOKEN" --arg p "$NEW_PASSWORD" '{email: $e, token: $t, newPassword: $p}')"
expect 204 "user 1 sets a new password"

user_token "$EMAIL1" "$PASSWORD"
expect 400 "old password no longer works"
user_token "$EMAIL1" "$NEW_PASSWORD"
expect 200 "new password works"

# ---------- delete ----------

request DELETE "/api/users/$USER2_ID" "" "$ADMIN"
expect 204 "admin deletes user 2"
user_token "$EMAIL2" "$PASSWORD"
expect 400 "deleted user can't sign in"
request GET "/api/users?page=1&pageSize=20" "" "$ADMIN"
[ "$(json .totalCount)" = "1" ] || fail "Expected 1 active user after delete, got: $RESPONSE"

expect_sql "SELECT IsDeleted FROM users.Users WHERE Id = '$USER2_ID'" 1 "user 2 is soft-deleted, row kept"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 2 "both sign-in accounts kept"
expect_sql "SELECT COUNT(*) FROM users.Users u JOIN auth.AspNetUsers a ON LOWER(CONVERT(nvarchar(36), u.Id)) = LOWER(a.Id) AND u.IdentityId = LOWER(a.Id)" 2 "each profile id equals its sign-in account id"
expect_sql "SELECT CASE WHEN COUNT(*) > 0 THEN 'yes' ELSE 'no' END FROM idsrv.Keys" yes "signing key stored in SQL"

# ---------- restart: migrations must leave data alone ----------

stop_app
start_app
[ "$(grep -c 'database schema is up to date' "$APP_LOG")" -ge 3 ] || fail "Expected all three schemas to be up to date on restart."
expect_sql "SELECT COUNT(*) FROM users.Users" 2 "profiles survive a restart"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 2 "accounts survive a restart"
user_token "$EMAIL1" "$NEW_PASSWORD"
expect 200 "user 1 still signs in after restart"
stop_app

# ---------- CAPTCHA rejected by Cloudflare ----------

# Cloudflare's "always fails" test secret makes Cloudflare reject every token, as it would a forged one.
start_app Captcha__TurnstileSecretKey=2x0000000000000000000000000000000AA
request POST /api/users "$(body '{email: "bot@example.com", password: "Passw0rd!", displayName: "Bot"}')"
expect 400 "register with a token Cloudflare rejects is refused"
[ "$(json '.errors["X-Captcha-Token"] | length')" -gt 0 ] || fail "Expected a CAPTCHA error, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'bot@example.com'" 0 "rejected CAPTCHA created no account"
stop_app

# ---------- summary ----------

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### users.Users after the smoke test"
    echo
    echo "| Email | DisplayName | IsDeleted |"
    echo "|---|---|---|"
    sql "SELECT Email + ' | ' + DisplayName + ' | ' + CAST(IsDeleted AS varchar(1)) FROM users.Users ORDER BY CreatedAt" \
      | sed 's/^/| /; s/$/ |/'
  } >> "$GITHUB_STEP_SUMMARY"
fi

# One annotation with every check, so the results show on the pull request.
checklist=$(printf '%s\n' "${PASSED[@]}")
rows=$(sql "SELECT Email + ' | ' + DisplayName + ' | IsDeleted=' + CAST(IsDeleted AS varchar(1)) FROM users.Users ORDER BY CreatedAt")
message=$(printf '%s checks passed:\n%s\n\nusers.Users:\n%s' "${#PASSED[@]}" "$checklist" "$rows")
echo "::notice title=Smoke test passed::$(sed ':a;N;$!ba;s/%/%25/g;s/\n/%0A/g' <<< "$message")"

echo "All smoke tests passed."
