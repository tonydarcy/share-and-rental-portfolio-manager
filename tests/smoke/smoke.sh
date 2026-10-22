#!/bin/bash
# Signs in as the demo user and opens every page (and every link on those pages), failing on any server error.
# Usage: tests/smoke/smoke.sh [base url]   (the site must be running against a database built by build-database.sh)
set -uo pipefail
cd "$(dirname "$0")/../.."
BASE="${1:-http://localhost:5075}"
JAR=$(mktemp); trap 'rm -f "$JAR"' EXIT
token() { grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//'; }

tok=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Account/Login" | token)
code=$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -w '%{http_code}' --data-urlencode "__RequestVerificationToken=$tok" \
    --data-urlencode "Email=${SMOKE_EMAIL:-demo@example.com}" --data-urlencode "Password=${SMOKE_PASSWORD:-demo-portfolio}" "$BASE/Account/Login")
[ "$code" = "302" ] || { echo "Sign-in failed (HTTP $code)"; exit 1; }

# Pages with no route parameters, from the @page lines
mapfile -t queue < <(cd src/PortfolioManager.Web/Pages && grep -rlx --include='*.cshtml' '@page' . \
    | sed -E 's#^\.##; s#\.cshtml$##; s#/Index$##; s#^$#/#' | grep -v '^/Error$' | sort)
declare -A seen; failed=0; count=0
skip='[Ll]ogout|[Ss]ign[Oo]ut|[Dd]elete|[Rr]emove|[Rr]evoke|^/api/|^/mcp|\.(css|js|png|ico|svg|webmanifest|woff2?)(\?|$)'
while [ ${#queue[@]} -gt 0 ] && [ $count -lt 2000 ]; do
    url="${queue[0]}"; queue=("${queue[@]:1}"); url="${url%%#*}"
    [ -n "${seen[$url]:-}" ] && continue; seen[$url]=1
    [[ "$url" =~ $skip ]] && continue
    count=$((count+1))
    body=$(curl -s -b "$JAR" -w '\n%{http_code}|%{content_type}|%{redirect_url}' "$BASE$url")
    IFS='|' read -r status ctype redirect <<<"$(tail -n1 <<<"$body")"
    if [ "$status" -ge 500 ] || [ "$status" = 404 ] || [ "$status" = 400 ]; then echo "HTTP $status  $url"; failed=$((failed+1)); continue; fi
    # The two-factor step sends you back to sign in when no sign-in is waiting for a code, which is right
    if [[ "$redirect" == *"/Account/Login"* && "$url" != /Account/LoginTwoFactor* ]]; then echo "Signed out by $url"; failed=$((failed+1)); continue; fi
    [[ "$ctype" == text/html* ]] || continue
    while read -r href; do [ -z "${seen[$href]:-}" ] && queue+=("$href"); done < <(grep -oE 'href="/[^"]*"' <<<"$body" | sed 's/^href="//;s/"$//;s/&amp;/\&/g' | sort -u)
done
echo "Opened $count pages, $failed failed."
[ $failed -eq 0 ]
