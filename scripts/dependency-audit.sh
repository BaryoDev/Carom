#!/bin/bash
# Carom Dependency Audit Script
# Validates that the core engine remains zero-dependency.

PROJECT_PATH="src/Carom/Carom.csproj"

echo "🔍 Auditing dependencies for: $PROJECT_PATH"

# Get the list of packages, excluding standard/implicit ones
# This command lists packages. If it returns anything other than an empty list (after filtering), we fail.
DEPENDENCIES=$(dotnet list "$PROJECT_PATH" package | grep -E "^   > " | grep -v "Microsoft.NET.Sdk" | grep -v "NETStandard.Library")

if [ -z "$DEPENDENCIES" ]; then
    echo "✅ Success: Zero external dependencies detected."
    exit 0
else
    echo "❌ Failure: Unauthorized dependencies found!"
    echo "$DEPENDENCIES"
    exit 1
fi
