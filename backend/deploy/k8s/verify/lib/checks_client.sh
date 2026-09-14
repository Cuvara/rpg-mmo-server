#!/usr/bin/env bash
# Layer 5 -- the real Unity client against this deployment.
#
# This suite deliberately does NOT launch Unity. Two Unity processes on one
# project fight over the Library lock and produce fake package-resolution
# errors; the Editor for IndieRPGMMOAdventure has exactly one driver. So the
# contract is: this layer PRINTS the exact invocation, and asserts on the NUnit
# XML the run produces. A missing XML is a visible SKIP, never a pass.

unity_command() {
  local proj="${VERIFY_UNITY_PROJECT:-E:\\SecretProject\\IndieRPGMMOAdventure}"
  cat <<CMD
        # Run from WSL: a \`VAR=value Unity.exe\` prefix does NOT cross into the
        # Windows process. Only WSLENV carries a variable over, and without it the
        # live test connects to its defaults (127.0.0.1:8000 / :7350), fails to
        # find a gateway there and reports Ignored -- against the WRONG address.
        export CUVARA_GATEWAY_HOST=${VERIFY_UNITY_GATEWAY_HOST} CUVARA_GATEWAY_PORT=${VERIFY_UNITY_GATEWAY_PORT}
        export CUVARA_NAKAMA_HOST=${VERIFY_UNITY_NAKAMA_HOST} CUVARA_NAKAMA_PORT=${VERIFY_UNITY_NAKAMA_PORT}
        export CUVARA_NAKAMA_SERVER_KEY=${VERIFY_NAKAMA_SERVER_KEY} CUVARA_MAP_ID=${VERIFY_MAP_ID}
        export WSLENV=CUVARA_GATEWAY_HOST:CUVARA_GATEWAY_PORT:CUVARA_NAKAMA_HOST:CUVARA_NAKAMA_PORT:CUVARA_NAKAMA_SERVER_KEY:CUVARA_MAP_ID
        # Resolve the editor from the project. Five are installed and globbing the
        # Hub directory picks 2021.3, which silently downgrades the asset database.
        VER=\$(sed -n 's/^m_EditorVersion: //p' <project>/ProjectSettings/ProjectVersion.txt)
        "/mnt/c/Program Files/Unity/Hub/Editor/\$VER/Editor/Unity.exe" \\
          -batchmode -nographics -projectPath ${proj} \\
          -runTests -testPlatform PlayMode \\
          -testResults ${VERIFY_UNITY_RESULTS:-<abs>\\playmode.xml} -logFile <abs>\\playmode.log
CMD
}

check_client_playmode() {
  local xml="${VERIFY_UNITY_RESULTS:-}"
  if [ -z "$xml" ] || [ ! -f "$xml" ]; then
    skip "no Unity PlayMode results at '${xml:-<VERIFY_UNITY_RESULTS unset>}'. The client is UNVERIFIED against this deployment -- this is not a pass. Run, then re-run this suite with VERIFY_UNITY_RESULTS pointing at the XML:
$(unity_command)"
    return
  fi
  local parsed
  parsed=$(python3 - "$xml" <<'PY'
import sys, xml.etree.ElementTree as ET
try:
    r = ET.parse(sys.argv[1]).getroot()
except Exception as e:
    print("PARSE_ERROR", e); sys.exit(0)
g = r.attrib.get
total, passed, failed = g("total","?"), g("passed","?"), g("failed","?")
skipped, inconc = g("skipped","0"), g("inconclusive","0")
print(f"COUNTS {total} {passed} {failed} {skipped} {inconc} {g('result','?')}")
def own_cats(node):
    return {p.attrib.get("value") for p in node.findall("./properties/property")
            if p.attrib.get("name") == "Category"}

live_cases = []
def walk(node, inherited):
    cats = inherited | own_cats(node)
    if node.tag == "test-case":
        if "LiveBackend" in cats:
            rsn = node.find("./reason/message")
            why = " ".join(rsn.text.split())[:200] if rsn is not None and rsn.text else ""
            live_cases.append((node.attrib.get("result"), node.attrib.get("fullname","?"), why))
        return
    for child in node:
        walk(child, cats)
walk(r, set())
for res, name, why in live_cases:
    print(f"LIVE {res} {name} :: {why}")

for tc in r.iter("test-case"):
    if tc.attrib.get("result") not in ("Passed", None):
        msg = ""
        m = tc.find("./failure/message")
        if m is not None and m.text:
            msg = " ".join(m.text.split())[:160]
        print(f"CASE {tc.attrib.get('result')} {tc.attrib.get('fullname','?')} :: {msg}")
PY
)
  if [[ "$parsed" == PARSE_ERROR* ]]; then
    fail "Unity results XML could not be parsed" "an NUnit3 result file" "$parsed" "$xml"
    return
  fi
  local counts total passed failed skipped inconc result
  counts=$(echo "$parsed" | grep '^COUNTS ' | head -1)
  read -r _ total passed failed skipped inconc result <<<"$counts"
  local cases
  cases=$(echo "$parsed" | grep '^CASE ' | sed 's/^CASE /        /')

  local age="(mtime $(date -r "$xml" '+%Y-%m-%d %H:%M:%S' 2>/dev/null))"
  if [ "${failed:-1}" != "0" ] || [ "$result" = "Failed" ]; then
    fail "Unity PlayMode tests failed against this deployment" \
      "failed=0" "total=$total passed=$passed failed=$failed skipped=$skipped inconclusive=$inconc result=$result $age" \
      "$xml, and the failing cases:
$cases"
    return
  fi
  if [ "${total:-0}" = "0" ] || [ "${passed:-0}" = "0" ]; then
    fail "Unity PlayMode run contains no executed tests" "passed > 0" \
      "total=$total passed=$passed $age" "$xml -- did the test filter match nothing?"
    return
  fi
  local minimum="${VERIFY_UNITY_MIN_TESTS:-0}"
  if [ "$minimum" != "0" ] && [ "$passed" -lt "$minimum" ]; then
    fail "fewer Unity tests ran than this target expects" "passed >= $minimum" \
      "passed=$passed total=$total $age" "$xml; VERIFY_UNITY_MIN_TESTS"
    return
  fi
  # The claim this check makes is about the LIVE-backend tests, so a green run in
  # which those were Ignored proves nothing about the deployment. That is not
  # hypothetical: a run producing total=30 passed=29 failed=0 -- reported PASS by
  # every assertion above -- had its single LiveBackend case Ignored because the
  # env vars never crossed the WSL/Windows boundary, so the client had connected
  # to nothing. Require at least one live case, and require it to have run.
  local live not_run
  live=$(echo "$parsed" | grep '^LIVE ' || true)
  if [ -z "$live" ]; then
    fail "no LiveBackend test in the Unity results" \
      "at least one test in category LiveBackend" \
      "total=$total passed=$passed, none categorised LiveBackend $age" \
      "$xml -- the run exercised only offline tests, so the client is UNVERIFIED against this deployment"
    return
  fi
  not_run=$(echo "$live" | grep -v '^LIVE Passed ' || true)
  if [ -n "$not_run" ]; then
    fail "the LiveBackend tests did not run against this deployment" \
      "every LiveBackend case Passed" \
      "$(echo "$not_run" | sed 's/^LIVE /        /')" \
      "$xml -- an Ignored live case usually means the client connected to its DEFAULT address, not this one. From WSL, export the CUVARA_* vars AND list them in WSLENV."
    return
  fi
  pass "total=$total passed=$passed failed=$failed skipped=$skipped inconclusive=$inconc $age; live: $(echo "$live" | wc -l) LiveBackend case(s) passed"
}

register client.playmode 5 "Unity PlayMode suite green against this deployment" \
  "the real client, with its real netcode package, completed its live-backend PlayMode tests against THIS gateway and Nakama -- asserted from the NUnit XML, not from a human reading a log" \
  "it proves nothing on its own if the XML is stale or was produced against another backend; the suite reports the file mtime so that is visible. It also never launches Unity -- the run is the operator's" \
  check_client_playmode
