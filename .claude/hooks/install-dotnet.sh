#!/bin/bash
# Cloud sessions only: install the .NET 10 SDK so builds and tests work without each session working it out.
[ "$CLAUDE_CODE_REMOTE" = "true" ] || exit 0
command -v dotnet >/dev/null 2>&1 && exit 0
(apt-get install -y -q dotnet-sdk-10.0 >/dev/null 2>&1 || (apt-get update -q >/dev/null 2>&1 && apt-get install -y -q dotnet-sdk-10.0 >/dev/null 2>&1)) \
  && echo ".NET SDK installed: $(dotnet --version)" \
  || echo "Could not install the .NET 10 SDK; install it before building."
exit 0
