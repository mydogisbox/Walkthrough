#!/bin/bash
set -e

current=$(grep -oE '[0-9]+\.[0-9]+\.[0-9]+' src/Directory.Build.props | head -1)
IFS='.' read -ra parts <<< "$current"
next="${parts[0]}.${parts[1]}.$((parts[2] + 1))"

echo "→ Packing $current"

dotnet pack src/Walkthrough.Core/Walkthrough.Core.csproj --output ./nupkgs -c Release -p:Version=$current
dotnet pack src/Walkthrough.Http/Walkthrough.Http.csproj  --output ./nupkgs -c Release -p:Version=$current
dotnet pack src/Walkthrough.Json/Walkthrough.Json.csproj  --output ./nupkgs -c Release -p:Version=$current

echo "→ Published $current to ./nupkgs"
echo "→ Bumping $current → $next"

sed -i '' "s/<Version>$current<\/Version>/<Version>$next<\/Version>/" src/Directory.Build.props
sed -i '' "s/Current version: $current/Current version: $next/" docs/claude/csharp-style.md

echo "→ Ready for $next"
