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
        CUVARA_GATEWAY_HOST=${VERIFY_UNITY_GATEWAY_HOST}  CUVARA_GATEWAY_PORT=${VERIFY_UNITY_GATEWAY_PORT}
        CUVARA_NAKAMA_HOST=${VERIFY_UNITY_NAKAMA_HOST}   CUVARA_NAKAMA_PORT=${VERIFY_UNITY_NAKAMA_PORT}
        CUVARA_NAKAMA_SERVER_KEY=${VERIFY_NAKAMA_SERVER_KEY}  CUVARA_MAP_ID=${VERIFY_MAP_ID}
        Unity.exe -batchmode -projectPath ${proj} \\
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
  pass "total=$total passed=$passed failed=$failed skipped=$skipped inconclusive=$inconc $age"
}

register client.playmode 5 "Unity PlayMode suite green against this deployment" \
  "the real client, with its real netcode package, completed its live-backend PlayMode tests against THIS gateway and Nakama -- asserted from the NUnit XML, not from a human reading a log" \
  "it proves nothing on its own if the XML is stale or was produced against another backend; the suite reports the file mtime so that is visible. It also never launches Unity -- the run is the operator's" \
  check_client_playmode
