#!/bin/sh
# Runs the port with card instrumentation. Navigate to Save Game, then close.
# Redirect rather than tee: PowerShell's tee writes UTF-16 and hides the log.
dotnet run --project GT2Port.csproj --no-build > card-probe.log 2>&1
echo "--- [CARD] lines ---"
grep '\[CARD\]' card-probe.log
