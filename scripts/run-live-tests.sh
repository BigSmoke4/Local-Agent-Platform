#!/usr/bin/env bash
set -euo pipefail
export LAP_RUN_LIVE_TESTS=1
: "${LAP_OLLAMA_URL:=http://localhost:11434}"
: "${LAP_OLLAMA_MODEL:=llama3.2:3b}"
dotnet test tests/LocalAgentPlatform.Integration.Tests/LocalAgentPlatform.Integration.Tests.csproj -c Release
