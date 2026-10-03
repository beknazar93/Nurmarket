#!/bin/sh
# Программа владельца NurMarket — та же программа, что и касса, с ключом --owner.
DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$DIR/NurMarketKassa.Avalonia" --owner "$@"
