#!/bin/sh
# Started by the base image as the "app" user (USER_ID/GROUP_ID), with HOME=/config.
set -u

cd /games 2>/dev/null || cd /config

/opt/packwright/Packwright &
APP_PID=$!
trap 'kill -TERM "$APP_PID" 2>/dev/null' TERM INT

# Packwright puts its window back where it was last time, which inside the browser leaves it off to one side.
# Once the main window is up, ask the window manager to maximize it so it fills the browser.
(
    i=0
    while [ "$i" -lt 120 ] && kill -0 "$APP_PID" 2>/dev/null; do
        if wmctrl -l 2>/dev/null | grep -q ' Packwright$'; then
            sleep 1
            wmctrl -F -r Packwright -b add,maximized_vert,maximized_horz 2>/dev/null
            break
        fi
        sleep 0.5
        i=$((i + 1))
    done
) &

wait "$APP_PID"
STATUS=$?
# A TERM interrupts the first wait; wait again for the app to finish closing.
kill -0 "$APP_PID" 2>/dev/null && wait "$APP_PID" && STATUS=$?
exit "$STATUS"
