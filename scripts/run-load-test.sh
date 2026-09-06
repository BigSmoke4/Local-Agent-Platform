#!/usr/bin/env bash
set -euo pipefail
: "${BASE_URL:=http://localhost:8080}"
: "${VUS:=20}"
: "${DURATION:=30s}"
k6 run -e BASE_URL="$BASE_URL" -e API_KEY="${API_KEY:-}" -e VUS="$VUS" -e DURATION="$DURATION" tests/load/agent-platform.js
