#!/bin/bash
set -e

# Virtual display for Wine - the tray icon is a GUI feature (see AGENTS.md's System tray
# section), so `wine Slopterm.Server.exe` needs a real X server to attach to even inside
# a headless container.
Xvfb :99 -screen 0 1024x768x24 &
export DISPLAY=:99

exec "$@"
