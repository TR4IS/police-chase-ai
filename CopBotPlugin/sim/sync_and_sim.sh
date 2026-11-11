#!/bin/bash
# Copy the plugin and simulator to the server, then build and run the simulator there. Touches nothing the game server uses.
# Run from the repo root.   Usage: SERVER=user@host sim/sync_and_sim.sh [only-scenarios-containing]
: "${SERVER:?set SERVER=user@host}"
cd "$(dirname "$0")/../.." || exit 1
scp -q CopBotPlugin/*.cs "$SERVER":build/AssettoServer_src/CopBotPlugin/ || exit 1
scp -q CopBotPlugin/lua/copbot_hud.lua "$SERVER":build/AssettoServer_src/CopBotPlugin/lua/ || exit 1
scp -q CopBotPlugin/sim/*.cs CopBotPlugin/sim/CopSim.csproj "$SERVER":build/CopSim/ || exit 1
ssh "$SERVER" "cd ~/build/CopSim && ~/.dotnet/dotnet build -c Release 2>&1 | grep -E 'error|Build succeeded|Warn.*CS8' | sort -u | head -20 && ~/.dotnet/dotnet bin/Release/net8.0/CopSim.dll ~/build/simout $1 2>&1 | cut -c1-200"
