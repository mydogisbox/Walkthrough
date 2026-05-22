#!/bin/bash
set -e

current=$(grep -oE '[0-9]+\.[0-9]+\.[0-9]+' src/Directory.Build.props | head -1)
IFS='.' read -ra parts <<< "$current"
version="${parts[0]}.${parts[1]}.$((parts[2] + 1))"

echo "→ Bumping $current → $version"

sed -i '' "s/<Version>$current<\/Version>/<Version>$version<\/Version>/" src/Directory.Build.props
sed -i '' "s/Current version: $current/Current version: $version/" docs/claude/csharp-style.md

dotnet pack src/Walkthrough.Core/Walkthrough.Core.csproj --output ./nupkgs -c Release -p:Version=$version
dotnet pack src/Walkthrough.Http/Walkthrough.Http.csproj  --output ./nupkgs -c Release -p:Version=$version
dotnet pack src/Walkthrough.Json/Walkthrough.Json.csproj  --output ./nupkgs -c Release -p:Version=$version

echo "→ Published $version to ./nupkgs"
