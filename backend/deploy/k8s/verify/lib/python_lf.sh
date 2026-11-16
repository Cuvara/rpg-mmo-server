#!/usr/bin/env bash
# Native Windows Python emits CRLF. Normalize output and preserve failures.
cluster_python_lf() (
  set -o pipefail
  python3 "$@" | tr -d '\r'
)
