#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Uruchom skrypt przez sudo: sudo $0" >&2
  exit 1
fi

echo "Ustawiam start systemu w trybie konsolowym (multi-user.target)..."
systemctl set-default multi-user.target

echo
echo "Domyślny target: $(systemctl get-default)"

echo
if systemctl cat display-manager.service >/dev/null 2>&1; then
  echo "Wykryto display-manager.service. GameDock będzie go uruchamiał i zatrzymywał przyciskiem GUI."
else
  echo "UWAGA: nie znaleziono display-manager.service."
  echo "Zainstaluj środowisko graficzne/display manager albo zmień komendy"
  echo "Machine:GraphicalInterface w appsettings.json na nazwę właściwej usługi."
fi

echo
echo "Zmiana zacznie obowiązywać przy następnym uruchomieniu."
echo "Nie wykonano restartu ani nie zatrzymano aktualnej sesji graficznej."
