#!/bin/sh
# 2026-10-29: ярлыки кассы и программы владельца в меню приложений Linux (без прав администратора).
# Запуск:  ./install-desktop.sh      Удаление ярлыков:  ./install-desktop.sh --remove
DIR="$(cd "$(dirname "$0")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
if [ "$1" = "--remove" ]; then
  rm -f "$APPS/nurmarket-kassa.desktop" "$APPS/nurmarket-owner.desktop" "$ICONS/nurmarket-kassa.png"
  echo "Ярлыки удалены."
  exit 0
fi
# Права на запуск (при распаковке не из tar.gz они могли потеряться).
chmod +x "$DIR/NurMarketKassa.Avalonia" "$DIR/nurmarket-owner.sh" 2>/dev/null
mkdir -p "$APPS" "$ICONS"
cp "$DIR/nurmarket-kassa.png" "$ICONS/nurmarket-kassa.png"
cat > "$APPS/nurmarket-kassa.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=NurCRM Kassa
Name[ru]=NurCRM Касса
Comment=Касса NurCRM (Нур СРМ)
Exec="$DIR/NurMarketKassa.Avalonia"
Path=$DIR
Icon=$ICONS/nurmarket-kassa.png
Terminal=false
Categories=Office;Finance;
StartupWMClass=NurMarketKassa.Avalonia
EOF
cat > "$APPS/nurmarket-owner.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=NurCRM Owner
Name[ru]=NurCRM Владелец
Comment=Программа владельца NurCRM
Exec="$DIR/NurMarketKassa.Avalonia" --owner
Path=$DIR
Icon=$ICONS/nurmarket-kassa.png
Terminal=false
Categories=Office;Finance;
StartupWMClass=NurMarketKassa.Avalonia
EOF
chmod +x "$APPS/nurmarket-kassa.desktop" "$APPS/nurmarket-owner.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" 2>/dev/null
echo "Готово: «NurCRM Касса» и «NurCRM Владелец» появились в меню приложений."
