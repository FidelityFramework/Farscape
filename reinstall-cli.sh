#!/bin/bash
set -e

export PATH="$PATH:/home/hhh/.dotnet/tools"

# Uninstall existing
dotnet tool uninstall -g Farscape 2>/dev/null || true

# Nuke ALL Release obj/bin caches to avoid MSBuild corruption
find /home/hhh/repos/Fidelity.Data /home/hhh/repos/Farscape \
  -path "*/obj/Release" -type d -exec rm -rf {} + 2>/dev/null || true
find /home/hhh/repos/Fidelity.Data /home/hhh/repos/Farscape \
  -path "*/bin/Release" -type d -exec rm -rf {} + 2>/dev/null || true

# Clean nupkg output
rm -rf /tmp/farscape-nupkg
mkdir -p /tmp/farscape-nupkg

# Restore + build + pack (fresh Release from scratch)
cd /home/hhh/repos/Farscape
dotnet restore Farscape.sln
dotnet pack src/Farscape.Cli/Farscape.Cli.fsproj -o /tmp/farscape-nupkg -c Release

# Install
dotnet tool install -g Farscape --add-source /tmp/farscape-nupkg --version "0.0.0-*"

echo ""
echo "=== Installed ==="
farscape --version 2>/dev/null || echo "(no --version flag, but tool is installed)"
