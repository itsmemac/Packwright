#!/bin/sh
# Runs as root at every start, before the app.
set -u

mkdir -p /temp
chown "$USER_ID:$GROUP_ID" /temp 2>/dev/null || true

# First start: begin with /games as the library and /temp for work files, so conversions do not fill the
# container's own filesystem (on Unraid that is docker.img). Both can be changed in Settings afterwards.
# Update checks start off and are not asked about: the container is updated by pulling a new image.
DATA="${XDG_DATA_HOME:-/config/xdg/data}/Packwright"
if [ ! -e "$DATA/settings.json" ]; then
    mkdir -p "$DATA"
    printf '{\n  "LibraryFolders": [ "/games" ],\n  "TempDirectory": "/temp",\n  "WindowMaximized": true,\n  "CheckForUpdates": false,\n  "UpdateCheckAsked": true\n}\n' > "$DATA/settings.json"
    chown -R "$USER_ID:$GROUP_ID" "${XDG_DATA_HOME:-/config/xdg/data}"
fi

# The browser shows one fixed-size screen, so the window always covers all of it. While a dialog is open the app
# makes its window non-resizable, which un-maximizes it; with the remembered size set to the whole screen it
# stays where it is instead of jumping back to wherever it was first placed.
W="${DISPLAY_WIDTH:-1920}"; H="${DISPLAY_HEIGHT:-1080}"
sed -i -E \
    -e "s/\"WindowWidth\": *-?[0-9]+/\"WindowWidth\": $W/" \
    -e "s/\"WindowHeight\": *-?[0-9]+/\"WindowHeight\": $H/" \
    -e 's/"WindowX": *-?[0-9]+/"WindowX": 0/' \
    -e 's/"WindowY": *-?[0-9]+/"WindowY": 0/' \
    -e 's/"WindowHasPosition": *(true|false)/"WindowHasPosition": true/' \
    -e 's/"WindowMaximized": *(true|false)/"WindowMaximized": true/' \
    "$DATA/settings.json"
if ! grep -q '"WindowWidth"' "$DATA/settings.json"; then
    sed -i -E "s/^\{/{\n  \"WindowWidth\": $W,\n  \"WindowHeight\": $H,\n  \"WindowX\": 0,\n  \"WindowY\": 0,\n  \"WindowHasPosition\": true,/" "$DATA/settings.json"
fi

# Sign-in is on but nobody can sign in yet: say how to add the account.
if [ "${WEB_AUTHENTICATION:-0}" = "1" ] \
   && [ -z "${WEB_AUTHENTICATION_USERNAME:-}" ] \
   && [ ! -s /config/webauth-htpasswd ]; then
    echo "[packwright] No sign-in account yet. Set WEB_AUTHENTICATION_USERNAME and WEB_AUTHENTICATION_PASSWORD,"
    echo "[packwright] or run: docker exec -it <container> webauth-user add <name>"
fi
