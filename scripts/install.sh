#!/bin/sh
# Frontier 15 Launcher for Linux (x86_64).
#
#   curl -fsSL https://github.com/Dark-Haven-inc/frontier15-launcher/releases/latest/download/install.sh | sh
#   curl -fsSL …/install.sh | sh -s -- --uninstall
#
# Puts the AppImage in ~/.local/share/Frontier15Launcher and starts it. On start the launcher adds
# itself to the application menu, registers ss14:// links and from then on updates itself.
# F15_APPIMAGE_URL overrides where the AppImage comes from (for testing a build).
set -eu

REPO="Dark-Haven-inc/frontier15-launcher"
URL="${F15_APPIMAGE_URL:-https://github.com/$REPO/releases/latest/download/Frontier15Launcher.AppImage}"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
DIR="$DATA/Frontier15Launcher"
APP="$DIR/Frontier15Launcher.AppImage"

say() { printf '%s\n' "$*"; }
die() { printf 'Ошибка: %s\n' "$*" >&2; exit 1; }

if [ "${1:-}" = "--uninstall" ]; then
    pkill -f "$APP" 2>/dev/null || true
    rm -rf "$DIR"
    rm -f "$DATA/applications/frontier15-launcher.desktop"
    mimeapps="${XDG_CONFIG_HOME:-$HOME/.config}/mimeapps.list"
    [ -f "$mimeapps" ] && sed -i '/=frontier15-launcher\.desktop;$/d' "$mimeapps"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DATA/applications" >/dev/null 2>&1 || true
    say "Лаунчер удалён. Настройки и кэш игры остались в $DATA/DarkHavenLauncher."
    exit 0
fi

[ "$(uname -s)" = Linux ] || die "этот установщик только для Linux"
case "$(uname -m)" in
    x86_64 | amd64) ;;
    *) die "нужен x86_64, а здесь $(uname -m)" ;;
esac

mkdir -p "$DIR"
tmp="$APP.part"
say "Загружаю Frontier 15 Launcher…"
if command -v curl >/dev/null 2>&1; then
    curl -fL --progress-bar -o "$tmp" "$URL" || die "не удалось скачать $URL"
elif command -v wget >/dev/null 2>&1; then
    wget -q --show-progress -O "$tmp" "$URL" || die "не удалось скачать $URL"
else
    die "нужен curl или wget"
fi
chmod 755 "$tmp"
mv -f "$tmp" "$APP"   # a rename: a launcher that is running keeps its old file
say "Установлен: $APP"

# The AppImage mounts itself with FUSE. Without fusermount it can still run by unpacking itself
# first (slower start); the launcher then writes the same into its menu entry.
if command -v fusermount3 >/dev/null 2>&1 || command -v fusermount >/dev/null 2>&1; then
    extract=""
else
    say "FUSE не найден: лаунчер будет распаковываться при каждом запуске (дольше старт)."
    extract=1
fi

say "Запускаю…"
if [ -n "$extract" ]; then
    APPIMAGE_EXTRACT_AND_RUN=1 nohup "$APP" >/dev/null 2>&1 &
else
    nohup "$APP" >/dev/null 2>&1 &
fi
