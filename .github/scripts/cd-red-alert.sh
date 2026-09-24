#!/usr/bin/env bash
# Keep one GitHub issue open while CD on develop is red, and close it on recovery.
#
# Why this exists: CD on develop failed on every run from 2026-09-13 to 2026-09-24 and
# nobody noticed, because CI stayed green beside it and a red run notifies no one. The
# eleven days hid a gameplay defect (players killed during the reconnect hold) behind the
# deploy failures in front of it. An open issue is a notification GitHub already sends
# to every watcher, and it needs no secret.
#
# Env:
#   REPO             owner/name
#   RUN_ID, RUN_URL  this workflow run
#   SHA              the commit deployed
#   DEPLOY_RESULT    needs.deploy.result
#   SMOKE_RESULT     needs.post-deploy-smoke.result
#   DISCORD_WEBHOOK_URL  optional; posts there too when set
set -euo pipefail

LABEL="cd-red"
short="${SHA:0:7}"

# Classify. `cancelled` is a newer push superseding this one (concurrency), not a
# failure, and says nothing either way. `skipped` on deploy means an upstream job
# (tests, build, backup) failed, so the deploy never ran -- that IS red.
# post-deploy-smoke is skipped by design in k8s mode, so only its failure counts.
verdict="green"
case "$DEPLOY_RESULT" in
  success) ;;
  cancelled) verdict="unknown" ;;
  *) verdict="red" ;;
esac
[ "$SMOKE_RESULT" = "failure" ] && verdict="red"

echo "deploy=$DEPLOY_RESULT smoke=$SMOKE_RESULT -> $verdict"
[ "$verdict" = "unknown" ] && exit 0

gh label create "$LABEL" --repo "$REPO" --color B60205 \
  --description "CD on develop is failing" >/dev/null 2>&1 || true

open_issue="$(gh issue list --repo "$REPO" --label "$LABEL" --state open \
  --json number --jq '.[0].number // empty')"

notify_discord() {
  [ -n "${DISCORD_WEBHOOK_URL:-}" ] || return 0
  local payload
  payload=$(python3 -c 'import json,sys; print(json.dumps({"content": sys.argv[1][:1900]}))' "$1")
  curl -fsS -H 'Content-Type: application/json' -d "$payload" "$DISCORD_WEBHOOK_URL" >/dev/null \
    || echo "::warning::Discord notification failed (the issue is the durable record)"
}

if [ "$verdict" = "red" ]; then
  failed="$(gh run view "$RUN_ID" --repo "$REPO" --json jobs \
    --jq '[.jobs[] | select(.conclusion=="failure") | .name] | join(", ")' 2>/dev/null || true)"
  [ -n "$failed" ] || failed="deploy did not run (upstream job failed)"
  line="CD on develop is **red** at \`${short}\` -- failed: ${failed}. ${RUN_URL}"

  if [ -z "$open_issue" ]; then
    gh issue create --repo "$REPO" --label "$LABEL" \
      --title "CD on develop is red" \
      --body "$(printf '%s\n\n%s\n\n%s\n' \
        "$line" \
        "This issue stays open while CD on \`develop\` is failing and closes itself on the next green deploy. Each further red run is added as a comment." \
        "CI can be green while CD is red: CD is the only pipeline that deploys to the dev cluster and runs the verification suite and the end-to-end smoke test against it.")"
  else
    gh issue comment "$open_issue" --repo "$REPO" --body "Still red: $line"
  fi
  notify_discord "$line"
  exit 0
fi

# green
if [ -n "$open_issue" ]; then
  gh issue comment "$open_issue" --repo "$REPO" \
    --body "Recovered: CD on develop is **green** at \`${short}\`. ${RUN_URL}"
  gh issue close "$open_issue" --repo "$REPO"
  notify_discord "CD on develop recovered at \`${short}\`. ${RUN_URL}"
fi
