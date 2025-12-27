#!/bin/bash

# Carom Manual Publishing Script
# Usage: ./publish.sh <version> [--dry-run] [--push]

VERSION=$1
DRY_RUN=false
PUSH=false

if [ -z "$VERSION" ]; then
    echo "Usage: ./publish.sh <version> [--dry-run] [--push]"
    exit 1
fi

for arg in "$@"; do
    if [ "$arg" == "--dry-run" ]; then
        DRY_RUN=true
    elif [ "$arg" == "--push" ]; then
        PUSH=true
    fi
done

echo "🚀 Preparing to publish Carom version $VERSION..."

# Update versions in .csproj files
if [ "$DRY_RUN" == "false" ]; then
    echo "📝 Updating project files to version $VERSION..."
    # Using sed to update <Version> tag
    sed -i '' "s|<Version>.*</Version>|<Version>$VERSION</Version>|" src/Carom/Carom.csproj
    sed -i '' "s|<Version>.*</Version>|<Version>$VERSION</Version>|" src/Carom.Http/Carom.Http.csproj
else
    echo "🔍 [DRY RUN] Would update .csproj files to version $VERSION"
fi

# Clean and Build
echo "📦 Building and packing..."
dotnet clean
dotnet build -c Release
dotnet pack -c Release -o ./dist /p:PackageVersion=$VERSION

if [ "$PUSH" == "true" ]; then
    if [ -z "$NUGET_API_KEY" ]; then
        echo "❌ Error: NUGET_API_KEY environment variable is not set."
        exit 1
    fi
    
    echo "⬆️ Pushing to NuGet.org..."
    dotnet nuget push "./dist/*.nupkg" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
else
    echo "✅ Pack complete. Use --push to deploy to NuGet.org (requires NUGET_API_KEY set)."
fi

echo "✨ Done!"
