#!/usr/bin/env bash
# End-to-end smoke test: starts the identity server against a real SQL Server and RabbitMQ, exercises the
# User module through its HTTP API and the token endpoint, checks the rows in the database and the events
# published to RabbitMQ, then restarts the app to prove startup migrations keep existing data and the
# retention job deletes accounts whose grace period has ended.
#
# Expects the app to be built (Release) and these environment variables:
#   ConnectionStrings__IdentityDatabase  connection string the app uses
#   SQL_CONTAINER, SQL_PASSWORD          docker container running SQL Server, and its sa password
#   ASPNETCORE_ENVIRONMENT=Development   (the local test clients only exist in Development)
#   Messaging__ConnectionUri             RabbitMQ the app publishes user lifecycle events to
# Optional, to check the published events through the RabbitMQ management API:
#   RABBITMQ_MANAGEMENT_URL, RABBITMQ_USER, RABBITMQ_PASSWORD
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

# jwt TOKEN JQ_EXPR  - reads a claim from an access token's payload
jwt() {
  local payload
  payload=$(cut -d. -f2 <<< "$1" | tr '_-' '/+')
  while [ $(( ${#payload} % 4 )) -ne 0 ]; do payload="$payload="; done
  base64 -d <<< "$payload" 2>/dev/null | jq -r "$2"
}

# location PATH  - sets STATUS and LOCATION without following redirects
location() {
  STATUS=$(curl -s -o /dev/null -w '%{http_code}' -D headers.txt "$BASE_URL$1")
  LOCATION=$(grep -i '^location:' headers.txt | sed 's/^[Ll]ocation: *//' | tr -d '\r')
}

body() { jq -cn "$@"; }

sql() {
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PASSWORD" -C \
    -d idsrv -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed '/^$/d'
}

# wait_for_sql QUERY EXPECTED LABEL  - polls for up to 60 seconds (for background work)
wait_for_sql() {
  local query=$1 expected=$2 label=$3 actual=""
  for _ in $(seq 1 30); do
    actual=$(sql "$query" | head -1)
    if [ "$actual" = "$expected" ]; then echo "ok  $label ($actual)"; PASSED+=("$label ($actual)"); return; fi
    sleep 2
  done
  fail "$label: expected '$expected', got '$actual' after 60 seconds"
}

# RabbitMQ management API: a test queue bound to the user events exchange collects every published event.
EVENTS_QUEUE=smoke-test.user-events
rabbit() {
  curl -fs -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" -H 'Content-Type: application/json' "$@"
}
rabbit_setup() {
  [ -n "${RABBITMQ_MANAGEMENT_URL:-}" ] || return 0
  rabbit -X PUT "$RABBITMQ_MANAGEMENT_URL/api/exchanges/%2F/bjk.users" -d '{"type":"fanout","durable":true}' > /dev/null
  rabbit -X PUT "$RABBITMQ_MANAGEMENT_URL/api/queues/%2F/$EVENTS_QUEUE" -d '{"durable":true}' > /dev/null
  rabbit -X POST "$RABBITMQ_MANAGEMENT_URL/api/bindings/%2F/e/bjk.users/q/$EVENTS_QUEUE" -d '{}' > /dev/null
  echo "ok  RabbitMQ test queue bound to bjk.users"
}
# expect_events COUNT LABEL  - waits until the test queue holds COUNT messages, then lists their types
expect_events() {
  [ -n "${RABBITMQ_MANAGEMENT_URL:-}" ] || { echo "--  skipped (no RABBITMQ_MANAGEMENT_URL): $2"; return 0; }
  local expected=$1 label=$2 actual=""
  for _ in $(seq 1 30); do
    actual=$(rabbit "$RABBITMQ_MANAGEMENT_URL/api/queues/%2F/$EVENTS_QUEUE" | jq -r '.messages // 0')
    [ "$actual" = "$expected" ] && break
    sleep 2
  done
  [ "$actual" = "$expected" ] || fail "$label: expected $expected messages on $EVENTS_QUEUE, got $actual"
  EVENT_TYPES=$(rabbit -X POST "$RABBITMQ_MANAGEMENT_URL/api/queues/%2F/$EVENTS_QUEUE/get" \
    -d "{\"count\":$expected,\"ackmode\":\"ack_requeue_true\",\"encoding\":\"auto\"}" | jq -r '[.[].payload | fromjson | .type] | join(",")')
  echo "ok  $label ($EVENT_TYPES)"
  PASSED+=("$label ($EVENT_TYPES)")
}

expect_sql() {
  local query=$1 expected=$2 label=$3 actual
  actual=$(sql "$query" | head -1)
  [ "$actual" = "$expected" ] || fail "$label: expected '$expected', got '$actual'"
  echo "ok  $label ($actual)"
  PASSED+=("$label ($actual)")
}

# ---------- first start: creates the database ----------

rabbit_setup
start_app
grep -q "applying migrations" "$APP_LOG" || fail "Expected the first start to apply migrations."
expect_sql "SELECT COUNT(*) FROM sys.schemas WHERE name IN ('auth', 'idsrv', 'users')" 3 "auth, idsrv and users schemas exist"

request GET /.well-known/openid-configuration
expect 200 "discovery document"
[ "$(json '.scopes_supported | index("bjk.posts") != null')" = "true" ] || fail "Expected bjk.posts in scopes_supported: $RESPONSE"
echo "ok  core API scopes are published"
expect_sql "SELECT COUNT(*) FROM auth.AspNetRoles WHERE Name IN ('Moderator', 'Admin')" 2 "Moderator and Admin roles exist"

request GET /Account/Login
expect 200 "sign-in page"
grep -q "Sign in" response.json || fail "The sign-in page doesn't look right."
request GET /Account/Register
expect 200 "sign-up page"
grep -q "cf-turnstile" response.json || fail "The sign-up page has no CAPTCHA widget."
location /Manage
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "/Account/Login" <<< "$LOCATION" || fail "Expected /Manage to redirect to sign-in, got $STATUS $LOCATION"
echo "ok  account pages need sign-in"
PASSED+=("account pages need sign-in (302)")
expect_sql "SELECT CASE WHEN COUNT(*) > 0 THEN 'yes' ELSE 'no' END FROM auth.DataProtectionKeys" yes "Data Protection keys stored in SQL"

CHALLENGE=$(printf 'smoke-test-verifier-0123456789abcdefghijklmnopq' | openssl dgst -sha256 -binary | base64 | tr '+/' '-_' | tr -d '=')
location "/connect/authorize?client_id=bjk-web&response_type=code&scope=openid%20profile%20email%20roles%20offline_access%20bjk.posts%20bjk.profiles%20bjk.media&redirect_uri=http%3A%2F%2Flocalhost%3A3000%2Fapi%2Fauth%2Fcallback%2Fbjk&code_challenge=$CHALLENGE&code_challenge_method=S256&state=abc"
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "/Account/Login" <<< "$LOCATION" || fail "Expected the web app's sign-in to land on the login page, got $STATUS $LOCATION"
echo "ok  the web app's sign-in goes to the login page"
PASSED+=("web client sign-in goes to the login page (302)")
location "/connect/authorize?client_id=bjk-web&response_type=code&scope=openid&redirect_uri=https%3A%2F%2Fevil.example.com%2Fcallback&code_challenge=$CHALLENGE&code_challenge_method=S256"
grep -q "/Error" <<< "$LOCATION" || [ "$STATUS" = "400" ] || fail "An unregistered redirect address wasn't refused: $STATUS $LOCATION"
echo "ok  unregistered redirect addresses are refused"
PASSED+=("unregistered redirect addresses are refused")

# ---------- CAPTCHA ----------

CAPTCHA=""
request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "register without a CAPTCHA token is rejected"
request POST /api/users/password-reset "$(body --arg e "$EMAIL1" '{email: $e}')"
expect 400 "forgot password without a CAPTCHA token is rejected"
request POST /api/users/reactivate "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "reactivate without a CAPTCHA token is rejected"

expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 0 "requests without a CAPTCHA created no account"

CAPTCHA=$DUMMY_CAPTCHA

# ---------- register ----------

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 201 "register user 1"
USER1_ID=$(json .id)

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "duplicate email is rejected"

request POST /api/users "$(body '{email: "weak@example.com", password: "short"}')"
expect 400 "weak password is rejected"
[ "$(json '.errors.Password | length')" -gt 0 ] || fail "Expected password errors, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'weak@example.com'" 0 "rejected registration left no account behind"

request POST /api/users "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 201 "register user 2"
USER2_ID=$(json .id)

expect_sql "SELECT COUNT(*) FROM users.Accounts a JOIN auth.AspNetUsers u ON LOWER(CONVERT(nvarchar(36), a.Id)) = LOWER(u.Id)" 2 "each account id equals its sign-in account id"

# ---------- confirm email ----------

sleep 1
CONFIRM_LINK=$(grep -o "ConfirmEmail?userId=$USER1_ID&code=[^ \"]*" "$APP_LOG" | tail -1)
[ -n "$CONFIRM_LINK" ] || fail "No confirmation email for user 1 in the app log."
grep -q "ConfirmEmail?userId=$USER2_ID&code=" "$APP_LOG" || fail "No confirmation email for user 2 in the app log."
echo "ok  registration sends a confirmation email"
PASSED+=("registration sends a confirmation email")
CONFIRM_CODE=$(sed 's/.*&code=//' <<< "$CONFIRM_LINK" | python3 -c 'import sys, urllib.parse; print(urllib.parse.unquote(sys.stdin.read().strip()))')

request POST /api/users/confirm-email "$(body --arg u "$USER1_ID" '{userId: $u, token: "not-a-real-code"}')"
expect 400 "a bad confirmation code is rejected"
request POST /api/users/confirm-email "$(body --arg u "$USER1_ID" --arg t "$CONFIRM_CODE" '{userId: $u, token: $t}')"
expect 204 "user 1 confirms their email"
expect_sql "SELECT CAST(EmailConfirmed AS int) FROM auth.AspNetUsers WHERE Id = '$USER1_ID'" 1 "user 1's email is confirmed"
location "/Account/ConfirmEmail?userId=$USER1_ID&code=x"
[ "$STATUS" = "200" ] || fail "The confirmation page didn't load: $STATUS"
echo "ok  confirmation page loads"
expect_sql "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('users.Accounts') AND name IN ('Email', 'DisplayName')" 0 "no email or display name copied into users.Accounts"

# ---------- sign in and self-service ----------

user_token "$EMAIL1" "$PASSWORD"
expect 200 "user 1 signs in"
TOKEN1=$(json .access_token)

request GET /api/users/me
expect 401 "no token is unauthorised"

[ "$(jwt "$TOKEN1" '.email_verified')" = "true" ] || fail "Expected email_verified=true in user 1's token: $(jwt "$TOKEN1" '.')"
echo "ok  user 1's token says their email is verified"
PASSED+=("token carries email_verified")

request GET /api/users/me "" "$TOKEN1"
expect 200 "user 1 reads own account"
[ "$(json .email)" = "$EMAIL1" ] || fail "Wrong account returned: $RESPONSE"
[ "$(json .status)" = "Active" ] || fail "Expected an active account: $RESPONSE"

request GET "/api/admin/users/$USER2_ID" "" "$TOKEN1"
expect 403 "user 1 can't read user 2"

request GET /api/admin/users "" "$TOKEN1"
expect 403 "user 1 can't list users"

# ---------- download my data ----------

request GET /api/me/data-export
expect 401 "data export needs a token"
request GET /api/me/data-export "" "$TOKEN1"
expect 200 "user 1 downloads their data"
[ "$(json .sections.account.email)" = "$EMAIL1" ] || fail "Export doesn't contain user 1's email: $RESPONSE"
[ "$(json .userId)" = "$USER1_ID" ] || fail "Export is for the wrong user: $RESPONSE"

# ---------- admin ----------

admin_token
expect 200 "admin client gets a token"
ADMIN=$(json .access_token)

request GET "/api/admin/users?page=1&pageSize=20" "" "$ADMIN"
expect 200 "admin lists users"
[ "$(json .totalCount)" = "2" ] || fail "Expected 2 users, got: $RESPONSE"
[ "$(json '[.items[].email] | sort | join(",")')" = "$EMAIL1,$EMAIL2" ] || fail "Expected both emails in the list: $RESPONSE"

# ---------- staff roles ----------

request PUT "/api/admin/users/$USER1_ID/roles" '{"roles":["Moderator"]}' "$TOKEN1"
expect 403 "users can't give themselves a role"
request PUT "/api/admin/users/$USER1_ID/roles" '{"roles":["Janitor"]}' "$ADMIN"
expect 400 "only Moderator and Admin roles exist"
request PUT "/api/admin/users/$USER1_ID/roles" '{"roles":["Moderator"]}' "$ADMIN"
expect 200 "admin makes user 1 a moderator"
[ "$(json '.roles | join(",")')" = "Moderator" ] || fail "Expected roles [Moderator]: $RESPONSE"

STATUS=$(curl -s -o response.json -w '%{http_code}' -X POST "$BASE_URL/connect/token" \
  -d grant_type=password -d client_id=bjk-local-user -d client_secret=local-user-secret \
  --data-urlencode "scope=openid roles bjk.users bjk.users.manage" \
  --data-urlencode "username=$EMAIL1" --data-urlencode "password=$PASSWORD")
RESPONSE=$(cat response.json)
expect 200 "moderator signs in without two-step"
MOD_TOKEN=$(json .access_token)
[ "$(jwt "$MOD_TOKEN" '.role')" = "Moderator" ] || fail "Expected role=Moderator in the token: $(jwt "$MOD_TOKEN" '.')"
echo "ok  the token carries the Moderator role"
PASSED+=("token carries the role")
request GET /api/admin/users "" "$MOD_TOKEN"
expect 403 "a moderator without two-step sign-in has no staff rights"

request PUT "/api/admin/users/$USER1_ID/roles" '{"roles":[]}' "$ADMIN"
expect 200 "admin removes the role"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUserRoles WHERE UserId = '$USER1_ID'" 0 "user 1 has no roles"

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

# ---------- deactivate and reactivate ----------

user_token "$EMAIL2" "$PASSWORD"
expect 200 "user 2 signs in"
TOKEN2=$(json .access_token)

request POST /api/users/me/deactivate "$(body '{password: "wrong-password"}')" "$TOKEN2"
expect 400 "deactivating needs the right password"

request POST /api/users/me/deactivate "$(body --arg p "$PASSWORD" '{password: $p}')" "$TOKEN2"
expect 204 "user 2 deactivates their account"
user_token "$EMAIL2" "$PASSWORD"
expect 400 "a deactivated user can't sign in"
expect_sql "SELECT Status FROM users.Accounts WHERE Id = '$USER2_ID'" Deactivated "user 2 is marked deactivated"
expect_sql "SELECT DATEDIFF(day, DeactivatedAt, DeletionDueAt) FROM users.Accounts WHERE Id = '$USER2_ID'" 90 "deletion is due 90 days later"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 2 "the sign-in account is kept during the grace period"

request POST /api/users/password-reset "$(body --arg e "$EMAIL2" '{email: $e}')"
expect 202 "forgot password for a deactivated account looks the same"

request POST /api/users/reactivate "$(body --arg e "$EMAIL2" '{email: $e, password: "wrong-password"}')"
expect 400 "reactivating needs the right password"
request POST /api/users/reactivate "$(body --arg e "$EMAIL1" --arg p "$NEW_PASSWORD" '{email: $e, password: $p}')"
expect 400 "an active account can't be reactivated"

request POST /api/users/reactivate "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 204 "user 2 reactivates within the grace period"
user_token "$EMAIL2" "$PASSWORD"
expect 200 "user 2 can sign in again"
expect_sql "SELECT CONCAT(Status, '/', ISNULL(CONVERT(varchar(30), DeletionDueAt), 'none')) FROM users.Accounts WHERE Id = '$USER2_ID'" "Active/none" "user 2 is active with no deletion due"

request POST "/api/admin/users/$USER2_ID/deactivate" "" "$TOKEN1"
expect 403 "users can't deactivate other accounts"
request POST "/api/admin/users/$USER2_ID/deactivate" "" "$ADMIN"
expect 204 "admin deactivates user 2"
user_token "$EMAIL2" "$PASSWORD"
expect 400 "user 2 can't sign in after the admin deactivates them"

request GET "/api/admin/users?status=Deactivated" "" "$ADMIN"
[ "$(json .totalCount)" = "1" ] || fail "Expected 1 deactivated user, got: $RESPONSE"
echo "ok  admin lists deactivated accounts"

wait_for_sql "SELECT COUNT(*) FROM users.Outbox WHERE PublishedAt IS NULL" 0 "every event is published to RabbitMQ"
expect_events 3 "events on the bjk.users exchange"
expect_sql "SELECT CASE WHEN COUNT(*) > 0 THEN 'yes' ELSE 'no' END FROM idsrv.Keys" yes "signing key stored in SQL"

# ---------- restart: migrations keep data; retention deletes expired accounts ----------

stop_app
# Pretend user 2's 90 days are up.
sql "UPDATE users.Accounts SET DeletionDueAt = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Id = '$USER2_ID'" > /dev/null
start_app
[ "$(grep -c 'database schema is up to date' "$APP_LOG")" -ge 3 ] || fail "Expected all three schemas to be up to date on restart."

wait_for_sql "SELECT COUNT(*) FROM users.Accounts WHERE Id = '$USER2_ID'" 0 "retention job deletes user 2's account record"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = '$EMAIL2'" 0 "and their sign-in account"
expect_sql "SELECT COUNT(*) FROM users.Accounts" 1 "user 1's account survives the restart"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 1 "user 1's sign-in account survives the restart"
wait_for_sql "SELECT COUNT(*) FROM users.Outbox WHERE PublishedAt IS NULL" 0 "the deletion event is published"
expect_events 4 "Deleted event on the bjk.users exchange"
user_token "$EMAIL1" "$NEW_PASSWORD"
expect 200 "user 1 still signs in after restart"
request POST /api/users/reactivate "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "a deleted account can't be reactivated"
stop_app

# ---------- CAPTCHA rejected by Cloudflare ----------

# Cloudflare's "always fails" test secret makes Cloudflare reject every token, as it would a forged one.
start_app Captcha__TurnstileSecretKey=2x0000000000000000000000000000000AA
request POST /api/users "$(body '{email: "bot@example.com", password: "Passw0rd!"}')"
expect 400 "register with a token Cloudflare rejects is refused"
[ "$(json '.errors["X-Captcha-Token"] | length')" -gt 0 ] || fail "Expected a CAPTCHA error, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'bot@example.com'" 0 "rejected CAPTCHA created no account"
stop_app

# ---------- summary ----------

ACCOUNTS_QUERY="SELECT CONCAT(LOWER(CONVERT(nvarchar(36), a.Id)), ' | ', u.Email, ' | ', a.Status) FROM users.Accounts a JOIN auth.AspNetUsers u ON LOWER(CONVERT(nvarchar(36), a.Id)) = LOWER(u.Id) ORDER BY a.CreatedAt"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### Accounts after the smoke test"
    echo
    echo "| Id | Email | Status |"
    echo "|---|---|---|"
    sql "$ACCOUNTS_QUERY" | sed 's/^/| /; s/$/ |/'
  } >> "$GITHUB_STEP_SUMMARY"
fi

# One annotation with every check, so the results show on the pull request.
checklist=$(printf '%s\n' "${PASSED[@]}")
rows=$(sql "$ACCOUNTS_QUERY")
message=$(printf '%s checks passed:\n%s\n\nAccounts:\n%s' "${#PASSED[@]}" "$checklist" "$rows")
echo "::notice title=Smoke test passed::$(sed ':a;N;$!ba;s/%/%25/g;s/\n/%0A/g' <<< "$message")"

echo "All smoke tests passed."
