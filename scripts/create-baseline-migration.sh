#!/usr/bin/env bash
set -euo pipefail
# Run once in a clean branch when you want a conventional EF migration history.
dotnet tool restore 2>/dev/null || true
dotnet ef migrations add InitialCreate \
  --project src/Shared/Data/Shared.Data.csproj \
  --startup-project src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj \
  --output-dir Migrations
