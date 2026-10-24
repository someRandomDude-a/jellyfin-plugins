#!/bin/sh
set -eu
npm install --prefix /tmp/theme-runner --no-audit --no-fund --loglevel=error --update-notifier=false playwright@1.56.1
export NODE_PATH=/tmp/theme-runner/node_modules
exec node /checks/theme-acceptance.cjs
