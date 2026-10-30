#!/bin/sh
set -eu
npm install --prefix /tmp/studio-runner --no-audit --no-fund --loglevel=error --update-notifier=false playwright@1.64.1
export NODE_PATH=/tmp/studio-runner/node_modules
failed=''
if node --test /checks/live-poster-browser.test.cjs; then :; else failed="$failed live-poster-browser"; fi
if node /checks/notification-studio-acceptance.cjs; then :; else failed="$failed notification-studio"; fi
if [ -n "$failed" ]; then
    printf 'FAILED test groups:%s\n' "$failed" >&2
    exit 1
fi
