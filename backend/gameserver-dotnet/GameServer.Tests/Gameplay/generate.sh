#!/usr/bin/env bash
#
# Regenerate the protoc reference bindings for Shared.GameLogic's gameplay.proto.
#
# These generated types exist ONLY to prove the hand-written codec in
# Shared.GameLogic/Gameplay/ byte-identical to protoc output
# (GameplayCodecParityTests). Nothing outside GameServer.Tests uses them, and the
# Unity client never sees them. The output is COMMITTED so neither CI nor a fresh
# clone needs protoc; run this only when gameplay.proto changes, then commit the
# result alongside the codec change.
#
# Requires protoc >= 25 (the repo is pinned to 29.3, matching the Google.Protobuf
# 3.29.3 runtime GameServer references). Set PROTOC to override the binary, e.g.
#   PROTOC=C:/Users/<you>/.local/tools/protoc-29.3/bin/protoc.exe ./generate.sh

set -euo pipefail

cd "$(dirname "$0")"
HERE="$(pwd)"
PROTO_DIR="$(cd ../../Shared.GameLogic/Gameplay && pwd)"
OUT="${HERE}/Generated"
PROTOC="${PROTOC:-protoc}"

command -v "$PROTOC" >/dev/null 2>&1 || {
  echo "error: $PROTOC not found (set PROTOC=/path/to/protoc)" >&2
  exit 1
}

echo "protoc: $("$PROTOC" --version)"

rm -rf "${OUT}"
mkdir -p "${OUT}"

"$PROTOC" \
  --proto_path="${PROTO_DIR}" \
  --csharp_out="${OUT}" \
  gameplay.proto

echo "generated:"
ls -1 "${OUT}"
