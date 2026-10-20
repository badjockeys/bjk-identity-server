#!/usr/bin/env bash
# End-to-end smoke test: starts the identity server against a real SQL Server, exercises the User module through
# its HTTP API and the token endpoint, checks the rows in the database and the events written to bjk-core's queue
# table (as bjk-core would read them), then restarts the app to prove startup migrations keep existing data and the
# retention job deletes accounts whose grace period has ended. Age policy (19 Oct 2026): sign-up asks for the date
# of birth (13+, under 13 refused and not kept; no selfie at sign-up), tokens carry minor=true for under-18s, and the
# age check is on demand for sensitive posts. It runs with the Development providers: an estimate of 30 passes; on
# the restart, estimates of 15 (and an ID saying 16) fail. The same restart trusts Cloudflare's CF-IPCountry header:
# sign-up from GB works, from FR or with no header it's refused.
#
# Expects the app to be built (Release) and these environment variables:
#   ConnectionStrings__IdentityDatabase  connection string the app uses
#   SQL_CONTAINER, SQL_PASSWORD          docker container running SQL Server, and its sa password
#   ASPNETCORE_ENVIRONMENT=Development   (the local test clients only exist in Development)
#   ConnectionStrings__Messaging         bjk-core's database (badjockeys) as MESSAGING_LOGIN, where events are queued
#   Messaging__ConnectionStringName=Messaging, Messaging__CreateTables=false
#   MESSAGING_LOGIN, MESSAGING_PASSWORD  the restricted login this script creates for that connection
# bjk-core doesn't run here, so the script creates its queue table and subscription the way bjk-core does on startup.
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5000}
APP_LOG=${APP_LOG:-app.log}
PROJECT=src/BJK.Idsrv.Host
: "${SQL_CONTAINER:?}" "${SQL_PASSWORD:?}"

EMAIL1=rider.one@example.com
EMAIL2=rider.two@example.com
EMAIL3=young.rider@example.com
EMAIL4=teen.rider@example.com
PASSWORD='Passw0rd!'
NEW_PASSWORD='N3wPassw0rd!'

# Dates of birth relative to today (UTC), as the identity server works them out.
ADULT_DOB=1990-06-15
TODAY=$(date -u +%F)
TEEN_DOB=$(date -u -d "$TODAY -15 years" +%F)           # 15 today
UNDER13_DOB=$(date -u -d "$TODAY -13 years +1 day" +%F) # 13 tomorrow: still 12 today

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
  if [ -n "${COUNTRY:-}" ]; then args+=(-H "CF-IPCountry: $COUNTRY"); fi
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

# upload PATH FIELD FILE CONTENT_TYPE [BEARER_TOKEN]  -> sets STATUS and RESPONSE (multipart file upload)
upload() {
  local args=(-s -o response.json -w '%{http_code}' -X POST "$BASE_URL$1" -F "$2=@$3;type=$4")
  if [ -n "${5:-}" ]; then args+=(-H "Authorization: Bearer $5"); fi
  STATUS=$(curl "${args[@]}")
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
  LOCATION=$(grep -i '^location:' headers.txt | sed 's/^[Ll]ocation: *//' | tr -d '\r' || true)
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

# bjk-core's side of the user events, which bjk-core creates on startup: the badjockeys database, its queue table
# [messaging].[bjk-core.user-events] (Rebus's SQL Server table plus the lease and delivery count columns) and, once
# "bjk-core starts", its subscription to the bjk.users topic. Plus the identity server's restricted login.
: "${MESSAGING_LOGIN:?}" "${MESSAGING_PASSWORD:?}"
EVENTS_TABLE='[messaging].[bjk-core.user-events]'
core_sql() {
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PASSWORD" -C -b \
    -d "${2:-badjockeys}" -h -1 -W -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed '/^$/d'
}
# as_messaging_login QUERY  - runs a query as the identity server's messaging login; fails if SQL Server refuses
as_messaging_login() {
  docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U "$MESSAGING_LOGIN" -P "$MESSAGING_PASSWORD" -C -b \
    -d badjockeys -h -1 -W -Q "SET NOCOUNT ON; $1" 2>&1 | tr -d '\r' | sed '/^$/d'
}
messaging_setup() {
  core_sql "IF DB_ID('badjockeys') IS NULL CREATE DATABASE badjockeys;" master > /dev/null
  core_sql "IF SCHEMA_ID('messaging') IS NULL EXEC('CREATE SCHEMA messaging');
    IF SCHEMA_ID('profiles') IS NULL EXEC('CREATE SCHEMA profiles');
    IF OBJECT_ID('profiles.Profiles') IS NULL CREATE TABLE profiles.Profiles (Id uniqueidentifier PRIMARY KEY, Handle nvarchar(30));
    IF OBJECT_ID('$EVENTS_TABLE') IS NULL CREATE TABLE $EVENTS_TABLE (
      [id] bigint IDENTITY(1,1) NOT NULL, [priority] int NOT NULL, [expiration] datetimeoffset NOT NULL,
      [visible] datetimeoffset NOT NULL, [headers] varbinary(max) NOT NULL, [body] varbinary(max) NOT NULL,
      [leaseduntil] datetimeoffset NULL, [leasedby] nvarchar(200) NULL, [leasedat] datetimeoffset NULL,
      [deliverycount] int NOT NULL DEFAULT 0,
      CONSTRAINT [PK_messaging_bjk-core.user-events] PRIMARY KEY CLUSTERED ([priority], [id]));
    IF OBJECT_ID('messaging.subscriptions') IS NULL CREATE TABLE messaging.subscriptions (
      [topic] nvarchar(200) NOT NULL, [address] nvarchar(200) NOT NULL,
      CONSTRAINT [PK_messaging_subscriptions] PRIMARY KEY CLUSTERED ([topic], [address]));" > /dev/null
  # The identity server's login: read the subscriptions, add queue rows, nothing else (see the bjk-modules README).
  core_sql "IF SUSER_ID('$MESSAGING_LOGIN') IS NULL CREATE LOGIN [$MESSAGING_LOGIN] WITH PASSWORD = '$MESSAGING_PASSWORD';" master > /dev/null
  core_sql "IF USER_ID('$MESSAGING_LOGIN') IS NULL CREATE USER [$MESSAGING_LOGIN] FOR LOGIN [$MESSAGING_LOGIN];
    GRANT SELECT ON OBJECT::[messaging].[subscriptions] TO [$MESSAGING_LOGIN];
    GRANT INSERT ON SCHEMA::[messaging] TO [$MESSAGING_LOGIN];" > /dev/null
  echo "ok  bjk-core's queue table and the identity server's messaging login exist"
}
core_subscribes() {
  core_sql "INSERT INTO messaging.subscriptions (topic, address) VALUES ('bjk.users', '$EVENTS_TABLE');" > /dev/null
  echo "ok  bjk-core subscribes its queue to bjk.users"
}
# expect_events COUNT LABEL  - waits until bjk-core's queue table holds COUNT events, then lists their types
expect_events() {
  local expected=$1 label=$2 actual=""
  for _ in $(seq 1 30); do
    actual=$(core_sql "SELECT COUNT(*) FROM $EVENTS_TABLE" | head -1)
    [ "$actual" = "$expected" ] && break
    sleep 2
  done
  [ "$actual" = "$expected" ] || fail "$label: expected $expected events in $EVENTS_TABLE, got $actual"
  EVENT_TYPES=$(core_sql "SELECT STRING_AGG(JSON_VALUE(CAST([body] AS varchar(max)), '\$.type'), ',') WITHIN GROUP (ORDER BY [id]) FROM $EVENTS_TABLE" | head -1)
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

messaging_setup
start_app
grep -q "applying migrations" "$APP_LOG" || fail "Expected the first start to apply migrations."
expect_sql "SELECT COUNT(*) FROM sys.schemas WHERE name IN ('auth', 'idsrv', 'users')" 3 "auth, idsrv and users schemas exist"

request GET /.well-known/openid-configuration
expect 200 "discovery document"
[ "$(json '.scopes_supported | index("bjk.posts") != null')" = "true" ] || fail "Expected bjk.posts in scopes_supported: $RESPONSE"
echo "ok  core API scopes are published"
[ "$(json '.scopes_supported | index("bjk.profiles.manage") != null')" = "true" ] || fail "Expected bjk.profiles.manage in scopes_supported: $RESPONSE"
echo "ok  bjk.profiles.manage is published"
PASSED+=("bjk.profiles.manage scope published")
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
if grep -q "evil.example.com" <<< "$LOCATION"; then fail "The browser was sent to an unregistered redirect address: $STATUS $LOCATION"; fi
echo "ok  unregistered redirect addresses are refused"
PASSED+=("unregistered redirect addresses are refused")

# ---------- CAPTCHA ----------

CAPTCHA=""
request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 400 "register without a CAPTCHA token is rejected"
request POST /api/users/password-reset "$(body --arg e "$EMAIL1" '{email: $e}')"
expect 400 "forgot password without a CAPTCHA token is rejected"
request POST /api/users/reactivate "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "reactivate without a CAPTCHA token is rejected"

expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 0 "requests without a CAPTCHA created no account"

CAPTCHA=$DUMMY_CAPTCHA

# ---------- register ----------

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 201 "register user 1"
USER1_ID=$(json .id)

request POST /api/users "$(body --arg e "$EMAIL1" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 400 "duplicate email is rejected"

request POST /api/users "$(body '{email: "weak@example.com", password: "short", dateOfBirth: "1990-06-15"}')"
expect 400 "weak password is rejected"
[ "$(json '.errors.Password | length')" -gt 0 ] || fail "Expected password errors, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'weak@example.com'" 0 "rejected registration left no account behind"

request POST /api/users "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 201 "register user 2"
USER2_ID=$(json .id)

# ---------- minimum age 13 ----------

request POST /api/users "$(body --arg e "$EMAIL4" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "sign-up without a date of birth is refused"
request POST /api/users "$(body --arg e "$EMAIL4" --arg p "$PASSWORD" --arg d "$UNDER13_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 400 "sign-up under 13 is refused"
grep -q "13 or over" <<< "$(json '.errors.DateOfBirth[0]')" || fail "Expected the kind under-13 message: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = '$EMAIL4'" 0 "an under-13 sign-up creates no account"
expect_sql "SELECT COUNT(*) FROM users.Accounts WHERE DateOfBirth = '$UNDER13_DOB'" 0 "and keeps no date of birth"

# The sign-up page: under 13 goes to the kind refusal page, with nothing created.
rm -f cookies.txt
curl -s -c cookies.txt -b cookies.txt "$BASE_URL/Account/Register" -o register.html
XSRF=$(grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' register.html | head -1 | sed 's/.*value="//; s/"$//')
[ -n "$XSRF" ] || XSRF=$(grep -o '__RequestVerificationToken[^>]*value="[^"]*"' register.html | head -1 | sed 's/.*value="//; s/"$//')
[ -n "$XSRF" ] || fail "No antiforgery token on the sign-up page."
grep -q 'autocomplete="bday-year"' register.html || fail "The sign-up page doesn't ask for the date of birth."
grep -q "<legend>Date of birth</legend>" register.html || fail "The date of birth isn't in a labelled fieldset."
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -D headers.txt -c cookies.txt -b cookies.txt -X POST "$BASE_URL/Account/Register" \
  --data-urlencode "__RequestVerificationToken=$XSRF" --data-urlencode "Input.Email=$EMAIL4" \
  --data-urlencode "Input.Password=$PASSWORD" --data-urlencode "Input.ConfirmPassword=$PASSWORD" \
  --data-urlencode "Input.DateOfBirth.Day=$(date -u -d "$UNDER13_DOB" +%-d)" \
  --data-urlencode "Input.DateOfBirth.Month=$(date -u -d "$UNDER13_DOB" +%-m)" \
  --data-urlencode "Input.DateOfBirth.Year=$(date -u -d "$UNDER13_DOB" +%Y)" \
  --data-urlencode "cf-turnstile-response=$DUMMY_CAPTCHA")
LOCATION=$(grep -i '^location:' headers.txt | sed 's/^[Ll]ocation: *//' | tr -d '\r' || true)
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "/Account/TooYoung" <<< "$LOCATION" || fail "Expected the sign-up page to send an under-13 to TooYoung, got $STATUS $LOCATION"
echo "ok  the sign-up page sends under-13s to the refusal page"
PASSED+=("sign-up page refuses under 13 (302)")
request GET /Account/TooYoung
expect 200 "the refusal page"
grep -q "13 or over" response.json || fail "The refusal page doesn't look right."
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = '$EMAIL4'" 0 "the refused sign-up page created no account"

request POST /api/users "$(body --arg e "$EMAIL4" --arg p "$PASSWORD" --arg d "$TEEN_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 201 "register a 15-year-old"
USER4_ID=$(json .id)
expect_sql "SELECT CONVERT(varchar(10), DateOfBirth, 23) FROM users.Accounts WHERE Id = '$USER4_ID'" "$TEEN_DOB" "the date of birth is stored on the account"

expect_sql "SELECT COUNT(*) FROM users.Accounts a JOIN auth.AspNetUsers u ON LOWER(CONVERT(nvarchar(36), a.Id)) = LOWER(u.Id)" 3 "each account id equals its sign-in account id"

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

# Tokens for the core API (audience bjk-api) carry the email, for report and appeal acknowledgements.
STATUS=$(curl -s -o response.json -w '%{http_code}' -X POST "$BASE_URL/connect/token" \
  -d grant_type=password -d client_id=bjk-local-user -d client_secret=local-user-secret \
  --data-urlencode "scope=openid bjk.posts" \
  --data-urlencode "username=$EMAIL1" --data-urlencode "password=$PASSWORD")
RESPONSE=$(cat response.json)
expect 200 "user 1 gets a core API token"
CORE_TOKEN=$(json .access_token)
[ "$(jwt "$CORE_TOKEN" '.email')" = "$EMAIL1" ] || fail "Expected the email in the core API token: $(jwt "$CORE_TOKEN" '.')"
[ "$(jwt "$CORE_TOKEN" '.aud | if type == "array" then index("bjk-api") != null else . == "bjk-api" end')" = "true" ] || fail "Expected audience bjk-api: $(jwt "$CORE_TOKEN" '.')"
echo "ok  core API tokens carry the email"
PASSED+=("bjk-api token carries email")

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
[ "$(json .sections.account.dateOfBirth)" = "$ADULT_DOB" ] || fail "Export doesn't contain user 1's date of birth: $RESPONSE"
echo "ok  the data export includes the date of birth"
PASSED+=("data export includes the date of birth")
[ "$(json .userId)" = "$USER1_ID" ] || fail "Export is for the wrong user: $RESPONSE"

# ---------- admin ----------

admin_token
expect 200 "admin client gets a token"
ADMIN=$(json .access_token)

request GET "/api/admin/users?page=1&pageSize=20" "" "$ADMIN"
expect 200 "admin lists users"
[ "$(json .totalCount)" = "3" ] || fail "Expected 3 users, got: $RESPONSE"
[ "$(json '[.items[].email] | sort | join(",")')" = "$EMAIL1,$EMAIL2,$EMAIL4" ] || fail "Expected every email in the list: $RESPONSE"

# ---------- under-18s: the minor claim; no age check for them ----------

user_token "$EMAIL4" "$PASSWORD"
expect 200 "the 15-year-old signs in straight after sign-up (no selfie at sign-up)"
TOKEN4=$(json .access_token)
[ "$(jwt "$TOKEN4" '.minor')" = "true" ] || fail "Expected minor=true in the 15-year-old's token: $(jwt "$TOKEN4" '.')"
[ "$(jwt "$TOKEN4" '.age_verified')" = "null" ] || fail "The 15-year-old's token shouldn't carry age_verified."
echo "ok  a 15-year-old's access token carries minor=true"
PASSED+=("15-year-old: minor=true in the access token")
STATUS=$(curl -s -o response.json -w '%{http_code}' -H "Authorization: Bearer $TOKEN4" "$BASE_URL/connect/userinfo")
RESPONSE=$(cat response.json)
expect 200 "userinfo for the 15-year-old"
[ "$(json .minor)" = "true" ] || fail "Expected minor=true from userinfo: $RESPONSE"
[ "$(jwt "$TOKEN1" '.minor')" = "null" ] || fail "An adult's token shouldn't carry minor: $(jwt "$TOKEN1" '.')"
echo "ok  an adult's token has no minor claim"
PASSED+=("adult: no minor claim")

request GET /api/users/me/age-check "" "$TOKEN4"
expect 200 "the 15-year-old reads their age check"
[ "$(json '(.underAgeByDateOfBirth | tostring) + "/" + (.canUseFaceCheck | tostring) + "/" + (.canUseDocumentCheck | tostring)')" = "true/false/false" ] || fail "Expected the age check not to be offered: $RESPONSE"
grep -q "only for adults" <<< "$(json .message)" || fail "Expected a kind explanation: $RESPONSE"
head -c 20000 /dev/urandom > selfie.webm
head -c 5000 /dev/urandom > id-photo.jpg
upload /api/users/me/age-check/face video selfie.webm video/webm "$TOKEN4"
expect 400 "an under-18 by date of birth can't start the age check"
upload /api/users/me/age-check/document image id-photo.jpg image/jpeg "$TOKEN4"
expect 400 "nor use photo ID"
expect_sql "SELECT CONCAT(AgeCheckStatus, '/', AgeCheckAttempts) FROM users.Accounts WHERE Id = '$USER4_ID'" "NotChecked/0" "no check ran for the under-18"

# Accounts from before dates of birth were asked for get no tokens until they give one.
sql "UPDATE users.Accounts SET DateOfBirth = NULL WHERE Id = '$USER4_ID'" > /dev/null
user_token "$EMAIL4" "$PASSWORD"
expect 400 "an older account with no date of birth gets no token"
sql "UPDATE users.Accounts SET DateOfBirth = '$TEEN_DOB' WHERE Id = '$USER4_ID'" > /dev/null
user_token "$EMAIL4" "$PASSWORD"
expect 200 "it gets tokens again once the date of birth is recorded"

# ---------- two-step secrets are encrypted at rest ----------

# The 15-year-old turns on an authenticator app through the real page: the key is shown, a code from it is accepted,
# and the stored authenticator key and recovery codes are encrypted (dp1: + Data Protection), never plain text.
rm -f cookies.txt
curl -s -c cookies.txt -b cookies.txt "$BASE_URL/Account/Login" -o login.html
XSRF=$(grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' login.html | head -1 | sed 's/.*value="//; s/"$//')
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -c cookies.txt -b cookies.txt -X POST "$BASE_URL/Account/Login" \
  --data-urlencode "__RequestVerificationToken=$XSRF" --data-urlencode "Input.Email=$EMAIL4" --data-urlencode "Input.Password=$PASSWORD")
[[ "$STATUS" =~ ^30[237]$ ]] || fail "The 15-year-old couldn't sign in on the sign-in page: $STATUS"
curl -s -c cookies.txt -b cookies.txt "$BASE_URL/Manage/EnableAuthenticator" -o authenticator.html
SHARED_KEY=$(grep -o '<span class="key">[^<]*</span>' authenticator.html | sed 's/<[^>]*>//g' | tr -d ' ')
[ -n "$SHARED_KEY" ] || fail "The authenticator page shows no key."
XSRF=$(grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' authenticator.html | head -1 | sed 's/.*value="//; s/"$//')
TOTP=$(python3 -c 'import base64,hmac,hashlib,struct,sys,time
k=sys.argv[1].upper(); k=base64.b32decode(k+"="*(-len(k)%8))
h=hmac.new(k,struct.pack(">Q",int(time.time())//30),hashlib.sha1).digest(); o=h[-1]&15
print("%06d"%((struct.unpack(">I",h[o:o+4])[0]&0x7fffffff)%1000000))' "$SHARED_KEY")
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -D headers.txt -c cookies.txt -b cookies.txt -X POST "$BASE_URL/Manage/EnableAuthenticator" \
  --data-urlencode "__RequestVerificationToken=$XSRF" --data-urlencode "Code=$TOTP")
LOCATION=$(grep -i '^location:' headers.txt | sed 's/^[Ll]ocation: *//' | tr -d '\r' || true)
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "ShowRecoveryCodes" <<< "$LOCATION" || fail "The authenticator code wasn't accepted: $STATUS $LOCATION"
echo "ok  an authenticator app is turned on with a code from its key"
PASSED+=("authenticator enrolment through the page (302)")
expect_sql "SELECT CAST(TwoFactorEnabled AS int) FROM auth.AspNetUsers WHERE Id = '$USER4_ID'" 1 "two-step is on for the 15-year-old"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUserTokens WHERE UserId = '$USER4_ID' AND LoginProvider = '[AspNetUserStore]' AND Name IN ('AuthenticatorKey', 'RecoveryCodes') AND Value LIKE 'dp1:%'" 2 "the authenticator key and recovery codes are stored encrypted"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUserTokens WHERE UserId = '$USER4_ID' AND Value LIKE '%$SHARED_KEY%'" 0 "the authenticator key isn't stored in plain text"

# ---------- age check, on demand for sensitive posts (Development providers: every estimate is 30) ----------

# selfie.webm and id-photo.jpg (above) stand in for the video and photo. The Development liveness check accepts any
# non-empty video.

[ "$(jwt "$TOKEN1" '.age_verified')" = "null" ] || fail "A new account's token shouldn't carry age_verified: $(jwt "$TOKEN1" '.')"
echo "ok  no age_verified after sign-up"
PASSED+=("no age_verified after sign-up")

request GET /api/users/me/age-check "" "$TOKEN1"
expect 200 "user 1 reads their age check"
[ "$(json '.status + "/" + (.attemptsLeft | tostring) + "/" + (.canUseFaceCheck | tostring)')" = "NotChecked/3/true" ] || fail "Expected NotChecked with 3 tries: $RESPONSE"

location /Account/CheckAge
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "/Account/Login" <<< "$LOCATION" || fail "Expected the age check page to need sign-in, got $STATUS $LOCATION"
echo "ok  the age check page needs sign-in"
PASSED+=("age check page needs sign-in (302)")

upload /api/users/me/age-check/face video selfie.webm video/webm
expect 401 "the age check needs a token"
upload /api/users/me/age-check/face video id-photo.jpg image/gif "$TOKEN1"
expect 400 "the age check refuses a file that isn't a video"

upload /api/users/me/age-check/face video selfie.webm video/webm "$TOKEN1"
expect 200 "user 1 passes the on-demand age check (Development estimator)"
[ "$(json .status)" = "Passed" ] || fail "Expected Passed: $RESPONSE"
expect_sql "SELECT CONCAT(AgeCheckStatus, '/', AgeCheckMethod, '/', AgeCheckResult, '/', AgeCheckProvider) FROM users.Accounts WHERE Id = '$USER1_ID'" "Passed/FaceEstimate/Passed/Development" "only the outcome is stored"
expect_sql "SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE SCHEMA_NAME(t.schema_id) = 'users' AND TYPE_NAME(c.system_type_id) IN ('varbinary', 'image')" 0 "no column could hold a video or photo"

user_token "$EMAIL1" "$PASSWORD"
expect 200 "user 1 signs in after passing"
TOKEN1=$(json .access_token)
[ "$(jwt "$TOKEN1" '.age_verified')" = "true" ] || fail "Expected age_verified=true in user 1's token: $(jwt "$TOKEN1" '.')"
echo "ok  the access token carries age_verified=true"
PASSED+=("access token carries age_verified=true")
STATUS=$(curl -s -o response.json -w '%{http_code}' -H "Authorization: Bearer $TOKEN1" "$BASE_URL/connect/userinfo")
RESPONSE=$(cat response.json)
expect 200 "userinfo"
[ "$(json .age_verified)" = "true" ] || fail "Expected age_verified=true from userinfo: $RESPONSE"

upload /api/users/me/age-check/face video selfie.webm video/webm "$TOKEN1"
expect 400 "a passed account isn't checked again"
request GET /api/users/me "" "$TOKEN1"
[ "$(json .ageVerified)" = "true" ] || fail "Expected ageVerified=true on the account: $RESPONSE"
request GET /api/me/data-export "" "$TOKEN1"
[ "$(json '.sections.account.ageCheck.status + "/" + .sections.account.ageCheck.method')" = "Passed/FaceEstimate" ] || fail "Expected the age check in the data export: $RESPONSE"
echo "ok  the data export includes the age check"
PASSED+=("data export includes the age check")

request GET "/api/admin/users/$USER1_ID" "" "$ADMIN"
expect 200 "admin reads user 1"
[ "$(json .ageCheck.status)" = "Passed" ] || fail "Expected the admin to see the age check status: $RESPONSE"
request POST "/api/admin/users/$USER1_ID/age-check/reset" "" "$TOKEN1"
expect 403 "users can't reset their own age check"
request POST "/api/admin/users/$USER1_ID/age-check/reset" "" "$ADMIN"
expect 200 "admin resets user 1's age check"
[ "$(json .ageCheck.status)" = "NotChecked" ] || fail "Expected NotChecked after the reset: $RESPONSE"
user_token "$EMAIL1" "$PASSWORD"
[ "$(jwt "$(json .access_token)" '.age_verified')" = "null" ] || fail "Expected no age_verified after the reset."
echo "ok  after a reset, tokens drop age_verified"
PASSED+=("reset removes age_verified")
upload /api/users/me/age-check/face video selfie.webm video/webm "$(json .access_token)"
expect 200 "user 1 passes again"

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
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 3 "the sign-in account is kept during the grace period"

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

# bjk-core hasn't subscribed yet, so the events wait in the outbox instead of being lost.
sleep 3
expect_sql "SELECT COUNT(*) FROM users.Outbox WHERE PublishedAt IS NULL" 3 "events wait in the outbox until bjk-core subscribes"
core_subscribes
wait_for_sql "SELECT COUNT(*) FROM users.Outbox WHERE PublishedAt IS NULL" 0 "every event is published to bjk-core's queue"
expect_events 3 "events in bjk-core's user events queue"
EVENT_ROW=$(core_sql "SELECT TOP 1 CONCAT(JSON_VALUE(CAST([body] AS varchar(max)), '\$.userId'), '|', [deliverycount], '|', CASE WHEN [leaseduntil] IS NULL AND [visible] <= SYSDATETIMEOFFSET() THEN 'ready' ELSE 'hidden' END) FROM $EVENTS_TABLE ORDER BY [id]" | head -1)
[ "${EVENT_ROW,,}" = "${USER2_ID,,}|0|ready" ] || fail "Expected the first event to be for user 2, unclaimed and ready for bjk-core: $EVENT_ROW"
echo "ok  events are ready for bjk-core to read (user id, unclaimed, visible)"
PASSED+=("events are ready for bjk-core to read")
if as_messaging_login "SELECT COUNT(*) FROM $EVENTS_TABLE" > /dev/null; then fail "The messaging login could read the queue."; fi
if as_messaging_login "SELECT COUNT(*) FROM profiles.Profiles" > /dev/null; then fail "The messaging login could read bjk-core's data."; fi
if as_messaging_login "DELETE FROM $EVENTS_TABLE" > /dev/null; then fail "The messaging login could delete queue rows."; fi
echo "ok  the messaging login can't read or delete queue rows or read bjk-core's data"
PASSED+=("messaging login limited to adding queue rows")
expect_sql "SELECT CASE WHEN COUNT(*) > 0 THEN 'yes' ELSE 'no' END FROM idsrv.Keys" yes "signing key stored in SQL"

# ---------- restart: migrations keep data; retention deletes expired accounts ----------

stop_app
# Pretend user 2's 90 days are up.
sql "UPDATE users.Accounts SET DeletionDueAt = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Id = '$USER2_ID'" > /dev/null
# A two-step secret stored in plain text, as before encryption at rest: the restart encrypts it.
sql "INSERT INTO auth.AspNetUserTokens (UserId, LoginProvider, Name, Value) VALUES ('$USER1_ID', '[AspNetUserStore]', 'AuthenticatorKey', 'PLAINTEXTKEYABCDEFGHIJKLMNOPQRSTU')" > /dev/null
start_app
[ "$(grep -c 'database schema is up to date' "$APP_LOG")" -ge 3 ] || fail "Expected all three schemas to be up to date on restart."

expect_sql "SELECT COUNT(*) FROM auth.AspNetUserTokens WHERE LoginProvider = '[AspNetUserStore]' AND Name IN ('AuthenticatorKey', 'RecoveryCodes') AND Value NOT LIKE 'dp1:%'" 0 "startup encrypts two-step secrets stored in plain text"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUserTokens WHERE Value LIKE '%PLAINTEXTKEY%'" 0 "the plain-text secret is gone"
grep -q "Encrypted 1 two-step secrets stored before encryption at rest" "$APP_LOG" || fail "Expected the backfill to log how many secrets it encrypted."
grep -q "Data Protection keys are stored in SQL without encryption at rest" "$APP_LOG" || fail "Expected the local-development warning about unencrypted Data Protection keys."

wait_for_sql "SELECT COUNT(*) FROM users.Accounts WHERE Id = '$USER2_ID'" 0 "retention job deletes user 2's account record"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = '$EMAIL2'" 0 "and their sign-in account"
expect_sql "SELECT COUNT(*) FROM users.Accounts" 2 "the other accounts survive the restart"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers" 2 "and their sign-in accounts"
wait_for_sql "SELECT COUNT(*) FROM users.Outbox WHERE PublishedAt IS NULL" 0 "the deletion event is published"
expect_events 4 "Deleted event in bjk-core's user events queue"
user_token "$EMAIL1" "$NEW_PASSWORD"
expect 200 "user 1 still signs in after restart"
request POST /api/users/reactivate "$(body --arg e "$EMAIL2" --arg p "$PASSWORD" '{email: $e, password: $p}')"
expect 400 "a deleted account can't be reactivated"
stop_app

# ---------- sign-up is UK-only; age check: under 18 ----------

# Restarted as if behind Cloudflare (the CF-IPCountry header is trusted), with Development age check providers
# that see someone under 18: an estimate of 15 and an ID saying 16.
start_app SignUp__TrustCloudflareCountryHeader=true AgeAssurance__Estimator__Development__Age=15 AgeAssurance__Document__Development__Age=16

COUNTRY=""
request POST /api/users "$(body --arg e "$EMAIL3" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 403 "sign-up without a country header is refused"
COUNTRY=FR
request POST /api/users "$(body --arg e "$EMAIL3" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 403 "sign-up from France is refused"
grep -q "only open for new accounts in the UK" <<< "$(json .detail)" || fail "Expected the UK-only message: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = '$EMAIL3'" 0 "refused sign-ups create no account"
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -D headers.txt -H "CF-IPCountry: FR" "$BASE_URL/Account/Register")
LOCATION=$(grep -i '^location:' headers.txt | sed 's/^[Ll]ocation: *//' | tr -d '\r' || true)
[[ "$STATUS" =~ ^30[237]$ ]] && grep -q "/Account/SignUpUnavailable" <<< "$LOCATION" || fail "Expected the sign-up page to send France to SignUpUnavailable, got $STATUS $LOCATION"
echo "ok  the sign-up page sends visitors from outside the UK to the friendly page"
PASSED+=("sign-up page refuses FR (302)")
request GET /Account/SignUpUnavailable
expect 200 "the sign-up unavailable page"
grep -q "You can still watch posts" response.json || fail "The sign-up unavailable page doesn't look right."
STATUS=$(curl -s -o response.json -w '%{http_code}' -X POST "$BASE_URL/connect/token" -H "CF-IPCountry: FR" \
  -d grant_type=password -d client_id=bjk-local-user -d client_secret=local-user-secret \
  --data-urlencode "scope=openid bjk.users" \
  --data-urlencode "username=$EMAIL1" --data-urlencode "password=$NEW_PASSWORD")
RESPONSE=$(cat response.json)
expect 200 "an existing account signs in from France"
grep -q "Sign-up country check: FR refused" "$APP_LOG" || fail "Expected the refusal to be logged with the country code."
grep -q "Sign-up country check: none refused" "$APP_LOG" || fail "Expected the missing header to be logged."
if grep "Sign-up country check" "$APP_LOG" | grep -qE "127\.0\.0\.1|::1"; then fail "The country check logged an IP address."; fi
echo "ok  the country check logs only the country and outcome"
PASSED+=("country check logs country and outcome, no IP")

COUNTRY=GB
request POST /api/users "$(body --arg e "$EMAIL3" --arg p "$PASSWORD" --arg d "$ADULT_DOB" '{email: $e, password: $p, dateOfBirth: $d}')"
expect 201 "register user 3 from the UK"
COUNTRY=""
USER3_ID=$(json .id)
user_token "$EMAIL3" "$PASSWORD"
expect 200 "user 3 signs in straight after sign-up"
TOKEN3=$(json .access_token)

upload /api/users/me/age-check/face video selfie.webm video/webm "$TOKEN3"
expect 200 "user 3's selfie is checked"
[ "$(json '.status + "/" + .result + "/" + (.canUseDocumentCheck | tostring)')" = "Failed/Underage/true" ] || fail "Expected an under-18 result with photo ID still offered: $RESPONSE"
grep -q "18 and over" <<< "$(json .message)" || fail "Expected a kind under-18 message: $RESPONSE"
upload /api/users/me/age-check/face video selfie.webm video/webm "$TOKEN3"
expect 400 "after an under-18 estimate the selfie can't be retried"
upload /api/users/me/age-check/document image id-photo.jpg image/jpeg "$TOKEN3"
expect 200 "user 3's photo ID is checked"
[ "$(json '.status + "/" + .method + "/" + (.canUseDocumentCheck | tostring)')" = "Failed/Document/false" ] || fail "Expected a final under-18 result: $RESPONSE"
upload /api/users/me/age-check/document image id-photo.jpg image/jpeg "$TOKEN3"
expect 400 "under 18 on photo ID is final"
user_token "$EMAIL3" "$PASSWORD"
expect 200 "user 3 can still sign in"
[ "$(jwt "$(json .access_token)" '.age_verified')" = "null" ] || fail "An under-18 account's token must not carry age_verified."
echo "ok  an under-18 account gets no age_verified claim"
PASSED+=("under 18: no age_verified")
expect_sql "SELECT CONCAT(AgeCheckStatus, '/', AgeCheckMethod, '/', AgeCheckAttempts) FROM users.Accounts WHERE Id = '$USER3_ID'" "Failed/Document/2" "user 3 is recorded as under 18, by photo ID, after 2 tries"

# The failed check offers "Contact support" with the attempt's reference, which is stored with the outcome.
REFERENCE=$(sql "SELECT AgeCheckReference FROM users.Accounts WHERE Id = '$USER3_ID'" | head -1)
[[ "$REFERENCE" =~ ^AGE-[A-HJ-KM-NP-Z2-9]{6}$ ]] || fail "Expected a stored reference like AGE-XXXXXX, got '$REFERENCE'"
echo "ok  the age check reference is stored with the outcome ($REFERENCE)"
PASSED+=("age check reference stored ($REFERENCE)")
grep -q "Age check $REFERENCE for account $USER3_ID: photo ID Underage" "$APP_LOG" || fail "Expected the reference in the log line recording the outcome."
rm -f cookies.txt
curl -s -c cookies.txt -b cookies.txt "$BASE_URL/Account/Login" -o login.html
XSRF=$(grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' login.html | head -1 | sed 's/.*value="//; s/"$//')
[ -n "$XSRF" ] || fail "No antiforgery token on the sign-in page."
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -c cookies.txt -b cookies.txt -X POST "$BASE_URL/Account/Login" \
  --data-urlencode "__RequestVerificationToken=$XSRF" --data-urlencode "Input.Email=$EMAIL3" --data-urlencode "Input.Password=$PASSWORD")
[[ "$STATUS" =~ ^30[237]$ ]] || fail "User 3 couldn't sign in on the sign-in page: $STATUS"
STATUS=$(curl -s -o check-age.html -w '%{http_code}' -c cookies.txt -b cookies.txt "$BASE_URL/Account/CheckAge?returnUrl=%2FManage")
[ "$STATUS" = "200" ] || fail "The age check page didn't load for user 3: $STATUS"
grep -q "Verify your age to view sensitive posts" check-age.html || fail "The age check page doesn't look right."
grep -q "$REFERENCE" check-age.html || fail "The failed age check page doesn't show the reference $REFERENCE."
SUPPORT_LINK=$(grep -o 'id="age-contact-support" href="[^"]*"' check-age.html | head -1)
grep -q 'mailto:support@badjockeys.com' <<< "$SUPPORT_LINK" && grep -q "Age%20check%20help%20%E2%80%93%20$REFERENCE" <<< "$SUPPORT_LINK" \
  || fail "The failed age check page has no prefilled Contact support link: $SUPPORT_LINK"
grep -q "young.rider%40example.com" <<< "$SUPPORT_LINK" || fail "The support email body doesn't name the account email."
grep -q "A person will look at your age check" check-age.html || fail "The support wording is missing."
echo "ok  the failed age check page offers Contact support with the reference"
PASSED+=("failed age check: Contact support link with reference")
stop_app

# ---------- CAPTCHA rejected by Cloudflare ----------

# Cloudflare's "always fails" test secret makes Cloudflare reject every token, as it would a forged one.
start_app Captcha__TurnstileSecretKey=2x0000000000000000000000000000000AA
request POST /api/users "$(body '{email: "bot@example.com", password: "Passw0rd!", dateOfBirth: "1990-06-15"}')"
expect 400 "register with a token Cloudflare rejects is refused"
[ "$(json '.errors["X-Captcha-Token"] | length')" -gt 0 ] || fail "Expected a CAPTCHA error, got: $RESPONSE"
expect_sql "SELECT COUNT(*) FROM auth.AspNetUsers WHERE Email = 'bot@example.com'" 0 "rejected CAPTCHA created no account"
stop_app

# ---------- email settings are checked at startup ----------

# Choosing a real provider without its keys must stop the app, not fail at the first sign-up.
check_refuses_to_start() {
  local label=$1 expected=$2; shift 2
  local mark; mark=$(wc -l < "$APP_LOG")
  env "$@" dotnet run --project "$PROJECT" --configuration Release --no-build --no-launch-profile >> "$APP_LOG" 2>&1 &
  local pid=$!
  for _ in $(seq 1 60); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
  if kill -0 "$pid" 2>/dev/null; then kill "$pid"; fail "$label: the app kept running."; fi
  tail -n +"$((mark + 1))" "$APP_LOG" | grep -q "$expected" || fail "$label: expected the log to say '$expected'."
  echo "ok  $label"; PASSED+=("$label")
}
check_refuses_to_start "won't start with SendGrid but no API key" "Email:SendGrid:ApiKey is required" Email__Provider=SendGrid
check_refuses_to_start "won't start without bjk-core's messaging connection" "Messaging:ConnectionStringName must name" Messaging__ConnectionStringName=
# The Development age check providers don't check anyone's age, so only Development may use them.
check_refuses_to_start "won't start outside Development with the Development age check" "is for local development only" ASPNETCORE_ENVIRONMENT=Production
check_refuses_to_start "won't start outside Development without trusting Cloudflare's country header" "SignUp:TrustCloudflareCountryHeader must be true" ASPNETCORE_ENVIRONMENT=Production
check_refuses_to_start "won't start outside Development without a certificate for the Data Protection keys" "DataProtection:Certificate is required outside Development" ASPNETCORE_ENVIRONMENT=Production
check_refuses_to_start "won't start with YuNet but no model" "must point to face_detection_yunet_2023mar.onnx" AgeAssurance__Liveness__Provider=YuNet

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
