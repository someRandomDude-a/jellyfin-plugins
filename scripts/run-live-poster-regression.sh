#!/bin/sh
set -eu
npm install --prefix /tmp/studio-runner --no-audit --no-fund --loglevel=error --update-notifier=false playwright@1.64.1
export NODE_PATH=/tmp/studio-runner/node_modules
exec node --test /checks/live-poster-browser.test.cjs
