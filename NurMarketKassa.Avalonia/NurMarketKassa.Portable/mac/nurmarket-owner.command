#!/bin/sh
# Программа владельца NurMarket — та же программа, что и касса, с ключом --owner.
APP="/Applications/NurCRM Kassa.app"
[ -d "$APP" ] || APP="$(cd "$(dirname "$0")" && pwd)/NurCRM Kassa.app"
exec open -n -a "$APP" --args --owner
