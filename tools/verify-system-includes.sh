#!/bin/bash
# Opt-in evidence script: Tier-1 system-include verification against a real
# MetaTrader installation (issue #179, S6). Drives a real LSP session that
# opens a document with `#include <Trade\Trade.mqh>` and asserts the symbols
# reached through that header produce zero undeclared-symbol diagnostics.
# It is NOT referenced by any CI workflow and never runs automatically.
#
# Usage:
#   ./tools/verify-system-includes.sh <path-to-MQL5/Include>

echo "=================================================="
echo "System Include Verification Script (issue #179, S6)"
echo "=================================================="
echo ""

# Check if we're in the right directory
if [ ! -f "src/MqlLanguageServer.Server.csproj" ]; then
    echo "❌ Error: Run this script from the project root directory"
    exit 1
fi

# The Python driver is standard-library only; only python3 and dotnet are needed.
if ! command -v python3 >/dev/null 2>&1; then
    echo "❌ Error: python3 is required but was not found in PATH"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$SCRIPT_DIR/verify-system-includes.py" "$@"
