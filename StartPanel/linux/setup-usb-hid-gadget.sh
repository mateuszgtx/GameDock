#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Uruchom skrypt jako root, np. przez systemd albo sudo." >&2
  exit 1
fi

GADGET_ROOT="/sys/kernel/config/usb_gadget"
GADGET_NAME="gamedock_hid"
GADGET_DIR="${GADGET_ROOT}/${GADGET_NAME}"
FUNCTION_NAME="hid.usb0"
CONFIG_NAME="c.1"
LANG="0x409"

modprobe dwc2
modprobe libcomposite

if ! mountpoint -q /sys/kernel/config; then
  mount -t configfs none /sys/kernel/config
fi

if [[ ! -d "${GADGET_ROOT}" ]]; then
  echo "Brak ${GADGET_ROOT}. Sprawdź moduły configfs/libcomposite." >&2
  exit 1
fi

# Skrypt jest idempotentny dla wcześniej poprawnie utworzonego gadżetu.
# Jeżeli katalog istnieje po nieudanej, częściowej konfiguracji, najprościej
# zrestartować Raspberry Pi i uruchomić skrypt ponownie.
if [[ -d "${GADGET_DIR}" ]]; then
  if [[ -f "${GADGET_DIR}/UDC" && -n "$(cat "${GADGET_DIR}/UDC")" ]]; then
    echo "USB HID gadget ${GADGET_NAME} jest już skonfigurowany."
    exit 0
  fi

  echo "Istnieje nieaktywny katalog ${GADGET_DIR}. Usuń niepełną konfigurację albo zrestartuj Raspberry Pi." >&2
  exit 1
fi

mkdir "${GADGET_DIR}"
cd "${GADGET_DIR}"

# Identyfikatory z przykładu dokumentacji Linux/Raspberry Pi dla gadżetów.
echo 0x1d6b > idVendor
echo 0x0104 > idProduct
echo 0x0100 > bcdDevice
echo 0x0200 > bcdUSB

mkdir -p "strings/${LANG}"
SERIAL="$(awk '/Serial/ {print $3}' /proc/cpuinfo | tail -n 1)"
[[ -n "${SERIAL}" ]] || SERIAL="gamedock-zero2w"
echo "${SERIAL}" > "strings/${LANG}/serialnumber"
echo "Raspberry Pi" > "strings/${LANG}/manufacturer"
echo "GameDock HID Keyboard" > "strings/${LANG}/product"

mkdir -p "configs/${CONFIG_NAME}/strings/${LANG}"
echo "GameDock HID" > "configs/${CONFIG_NAME}/strings/${LANG}/configuration"
echo 250 > "configs/${CONFIG_NAME}/MaxPower"

# 0x80 = wymagany bit USB, 0x20 = Remote Wakeup capability.
# Sam bit nie gwarantuje wybudzenia hosta; zależy to też od UDC, kernela i BIOS/UEFI.
echo 0xA0 > "configs/${CONFIG_NAME}/bmAttributes"

mkdir "functions/${FUNCTION_NAME}"
echo 1 > "functions/${FUNCTION_NAME}/protocol"
echo 1 > "functions/${FUNCTION_NAME}/subclass"
echo 8 > "functions/${FUNCTION_NAME}/report_length"

# Standardowy 8-bajtowy raport klawiatury z dokumentacji Linux HID Gadget.
printf '%b' \
  '\x05\x01\x09\x06\xa1\x01\x05\x07\x19\xe0\x29\xe7\x15\x00\x25\x01'\
  '\x75\x01\x95\x08\x81\x02\x95\x01\x75\x08\x81\x03\x95\x05\x75\x01'\
  '\x05\x08\x19\x01\x29\x05\x91\x02\x95\x01\x75\x03\x91\x03\x95\x06'\
  '\x75\x08\x15\x00\x25\x65\x05\x07\x19\x00\x29\x65\x81\x00\xc0' \
  > "functions/${FUNCTION_NAME}/report_desc"

ln -s "functions/${FUNCTION_NAME}" "configs/${CONFIG_NAME}/${FUNCTION_NAME}"

UDC="$(ls /sys/class/udc | head -n 1)"
if [[ -z "${UDC}" ]]; then
  echo "Nie znaleziono kontrolera UDC. Sprawdź dtoverlay=dwc2,dr_mode=peripheral i zrestartuj Raspberry Pi." >&2
  exit 1
fi

echo "${UDC}" > UDC

udevadm settle || true

# Projektowa usługa już używa grupy gpio. Ustawiamy tę samą grupę również
# dla /dev/hidg*, aby aplikacja nie musiała działać jako root.
if getent group gpio >/dev/null 2>&1; then
  for device in /dev/hidg*; do
    [[ -e "${device}" ]] || continue
    chgrp gpio "${device}"
    chmod 0660 "${device}"
  done
fi

echo "USB HID gadget gotowy."
ls -l /dev/hidg* 2>/dev/null || true
