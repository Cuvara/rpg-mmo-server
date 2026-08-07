#!/usr/bin/env bash
#
# Regenerate the Go and C# wire protocol bindings from wire.proto.
#
# wire.proto is the single source of truth for the realtime wire format. Both
# outputs are COMMITTED so that neither CI runner nor a fresh clone needs protoc;
# this script is only run when the schema changes.
#
# Requires:
#   protoc         >= 25  (https://github.com/protocolbuffers/protobuf/releases)
#   protoc-gen-go  v1.36.6 (go install google.golang.org/protobuf/cmd/protoc-gen-go@v1.36.6)
#
# The C# generator is built into protoc, so no extra plugin is needed there.
#
# After running, `git diff --exit-code` over the generated directories is what
# CI uses to prove the committed code still matches the schema (see the
# `proto-generated-up-to-date` job in .github/workflows/ci.yml).

set -euo pipefail

cd "$(dirname "$0")"
PROTO_DIR="$(pwd)"
REPO_BACKEND="$(cd ../.. && pwd)"

GO_OUT="${PROTO_DIR}/gen"
CS_OUT="${REPO_BACKEND}/gameserver-dotnet/GameServer/Net/Generated"

for tool in protoc protoc-gen-go; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "error: $tool not found in PATH" >&2
    exit 1
  }
done

echo "protoc:        $(protoc --version)"
echo "protoc-gen-go: $(protoc-gen-go --version 2>&1)"

rm -rf "${GO_OUT}" "${CS_OUT}"
mkdir -p "${GO_OUT}" "${CS_OUT}"

# --- Go ---
# paths=source_relative keeps the output flat in gen/ instead of recreating the
# full go_package path underneath it.
protoc \
  --proto_path="${PROTO_DIR}" \
  --go_out="${GO_OUT}" \
  --go_opt=paths=source_relative \
  wire.proto

# --- C# ---
# base_namespace= (empty) stops protoc from nesting the output in a directory
# tree mirroring csharp_namespace.
protoc \
  --proto_path="${PROTO_DIR}" \
  --csharp_out="${CS_OUT}" \
  --csharp_opt=base_namespace= \
  wire.proto

echo
echo "generated:"
echo "  Go: ${GO_OUT}/wire.pb.go"
echo "  C#: ${CS_OUT}/Wire.cs"
