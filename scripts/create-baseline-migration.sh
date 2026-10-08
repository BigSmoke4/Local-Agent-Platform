#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 8 SDK is required to generate the EF Core baseline migration." >&2
  exit 127
fi

migration_dir="src/Shared/Data/Migrations"
if [[ -d "$migration_dir" ]] && find "$migration_dir" -maxdepth 1 -type f -name '*.cs' -print -quit | grep -q .; then
  echo "A migration already exists in $migration_dir; refusing to scaffold a second baseline." >&2
  exit 1
fi

dotnet tool restore
dotnet ef migrations add InitialCreate \
  --context PlatformDbContext \
  --project src/Shared/Data/Shared.Data.csproj \
  --startup-project src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj \
  --output-dir Migrations
