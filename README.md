GameDock

GameDock is a web-based control panel for a multi-boot computer and an optional NAS server. It provides a simple web interface and API for checking device status, powering devices on, restarting or shutting them down, selecting an operating system through GRUB, and reading telemetry from external agents.

The project is built with ASP.NET Core / .NET 10. The frontend is located directly in StartPanel/wwwroot and is served by the same application.

Key Features

power on the computer using Wake-on-LAN or USB HID Gadget,

detect the currently running operating system,

restart and shut down systems over SSH,

select the next operating system with grub-reboot,

automatic sequence: “power on computer → start boot manager system → select target system”,

start or stop the local Linux graphical interface (display-manager),

physical GPIO buttons for selecting an operating system and toggling the GUI,

NAS support: status, Wake-on-LAN, restart, and shutdown,

computer and NAS telemetry through GameDock.Agent,

responsive web control panel,

rate limiting for power and restart operations.

Project Structure

GameDock-master/
├── StartPanel.slnx
├── StartPanel/
│   ├── Program.cs
│   ├── StartPanel.csproj
│   ├── appsettings.json
│   ├── Options/              # configuration models
│   ├── Services/             # PC, NAS, GPIO, WoL, and HID control logic
│   ├── wwwroot/              # HTML/CSS/JS frontend
│   └── linux/                # example systemd units and Linux scripts
└── README.md

Requirements

Basic requirements:

.NET 10 SDK to build the project,

.NET 10 Runtime / ASP.NET Core Runtime on the target device for a framework-dependent deployment,

a local network that allows access to the controlled computer.

Depending on the features you use, you may also need:

SSH access to the controlled systems,

grub-reboot on the system acting as the boot manager,

Wake-on-LAN enabled in BIOS/UEFI and the operating system,

Raspberry Pi / Linux with GPIO support for physical buttons,

Linux with USB Gadget and UDC support for UsbHid mode,

GameDock.Agent on controlled systems if detailed telemetry is required.

Quick Start

Configure StartPanel/appsettings.json.

From the repository directory, run:

dotnet restore StartPanel/StartPanel.csproj
dotnet run --project StartPanel/StartPanel.csproj --no-launch-profile

Open the control panel in your browser:

http://localhost:5080

By default, the application listens on http://0.0.0.0:5080, so when it is running on another device in your network, the panel will be available through that device's IP address, for example http://192.168.1.50:5080.

Configuration

The main configuration file is:

StartPanel/appsettings.json

1. Computer Power-On Method

The PowerControl section selects how the power-on signal is sent:

"PowerControl": {
  "StartupMethod": "WakeOnLan",
  "HidDevice": "/dev/hidg0",
  "HidKeyCode": 44,
  "HidPressDurationMs": 80,
  "HidWriteTimeoutMs": 1000
}

Available StartupMethod values:

WakeOnLan — sends a magic packet to the computer's MAC address,

UsbHid — writes a keyboard HID report to a /dev/hidgX device.

2. Controlled Computer

Important fields in the Machine section:

"Machine": {
  "Host": "192.168.1.100",
  "BroadcastAddress": "192.168.1.255",
  "MacAddress": "AA:BB:CC:DD:EE:FF",
  "WakePort": 9,
  "BootManagerSystemId": "linux",
  "Systems": []
}

Host — address used to check whether the computer is reachable,

BroadcastAddress and MacAddress — Wake-on-LAN settings,

BootManagerSystemId — ID of the operating system that has access to GRUB,

Systems — list of operating systems that can be detected and controlled.

3. Operating System Definition

Example system entry:

{
  "Id": "linux",
  "Name": "Linux",
  "GrubEntry": "0",
  "SshHost": "192.168.1.100",
  "SshPort": 22,
  "SshUser": "admin",
  "SshPassword": "",
  "DetectionCommand": "cat /etc/os-release",
  "DetectionContains": "ID=",
  "CanSwitchFrom": true,
  "CanRestart": true,
  "AgentUrl": "http://192.168.1.100:7070"
}

GrubEntry can be a single number such as 1, or an entry inside a submenu such as 1>2.

CanSwitchFrom should be true only for systems from which grub-reboot can be executed. For Windows, it will usually be false.

Important: Fix the linux System ID

In the supplied appsettings.json, the following fields refer to a system with the ID linux:

Machine:BootManagerSystemId,

Machine:GraphicalInterface:SystemId,

one of the GpioButtons:Buttons[].SystemId entries.

However, the current Machine:Systems array contains only arch and windows entries.

Before using the boot sequence, Linux GPIO button, or graphical interface controls, add a linux system to Machine:Systems, or change these IDs so that they point to an existing system.

GRUB Control

Operating system switching uses grub-reboot, which selects an entry for the next boot only.

Default command:

sudo /usr/bin/grub-reboot

The SSH user on the boot manager system must have the required sudo permission. The project contains an example sudoers file:

StartPanel/linux/99-wolf-control

After adjusting the username, you can install it with:

sudo cp StartPanel/linux/99-wolf-control /etc/sudoers.d/99-wolf-control
sudo chmod 0440 /etc/sudoers.d/99-wolf-control
sudo visudo -c

Do not grant broader privileges than necessary.

Linux Graphical Interface

GameDock can start and stop display-manager, allowing Linux to boot into console mode by default while still letting you enable the GUI from the web panel or a GPIO button.

Helper script:

sudo StartPanel/linux/configure-console-boot.sh

Default configured commands:

/usr/bin/systemctl is-active display-manager
sudo /usr/bin/systemctl start display-manager
sudo /usr/bin/systemctl stop display-manager

GPIO

Physical button support is configured in the GpioButtons section:

"GpioButtons": {
  "Enabled": true,
  "PollIntervalMilliseconds": 20,
  "DebounceMilliseconds": 70,
  "ShutdownHoldMilliseconds": 2000,
  "Buttons": [
    {
      "Pin": 17,
      "SystemId": "linux",
      "Name": "Linux"
    }
  ],
  "GraphicalButton": {
    "Pin": 23,
    "Name": "GUI"
  }
}

Pin numbers use BCM numbering. Buttons should connect the GPIO input to GND when pressed.

If the application is running on a machine without GPIO support, set:

"GpioButtons": {
  "Enabled": false
}

USB HID Gadget

UsbHid mode is intended for Linux devices such as a Raspberry Pi with USB Gadget support.

The project includes:

StartPanel/linux/setup-usb-hid-gadget.sh
StartPanel/linux/gamedock-usb-hid.service.example
StartPanel/linux/99-gamedock-hid.rules

The setup script creates a keyboard HID device and exposes /dev/hidg0. The default key code 44 (0x2C) corresponds to the Space key.

This requires a working UDC, configfs, libcomposite, and appropriate hardware/bootloader configuration. On Raspberry Pi, it may also be necessary to enable dwc2 in peripheral mode.

NAS

NAS support is optional. Example configuration:

"Nas": {
  "Enabled": true,
  "Name": "NAS",
  "Host": "192.168.1.20",
  "BroadcastAddress": "192.168.1.255",
  "MacAddress": "AA:BB:CC:DD:EE:11",
  "WakePort": 9,
  "SshHost": "192.168.1.20",
  "SshPort": 22,
  "SshUser": "admin",
  "SshPassword": "",
  "ShutdownCommand": "sudo -n /usr/bin/systemctl poweroff",
  "RestartCommand": "sudo -n /usr/bin/systemctl reboot",
  "AgentUrl": "http://192.168.1.20:7070",
  "AgentTimeoutMs": 4000
}

If Nas:Enabled is false, or the section is missing, NAS support remains disabled.

Telemetry / GameDock.Agent

For each operating system, you can configure:

"AgentUrl": "http://IP:7070"

StartPanel retrieves data from:

GET {AgentUrl}/api/stats

The agent is used to retrieve information such as CPU usage, temperatures, memory, disk usage, network activity, and GPU statistics. The NAS uses a similar endpoint and can additionally display storage, disk, and SMART information.

The GameDock.Agent source code is not included in this package. Without a running agent, the panel can still control devices, but detailed telemetry will not be available.

Running as a systemd Service

First, publish the application:

dotnet publish StartPanel/StartPanel.csproj -c Release -o publish

Copy the contents of the publish directory, for example to:

/opt/gamedock

The project contains an example systemd unit:

StartPanel/linux/gamedock.service.example

After adjusting it for your environment:

sudo cp StartPanel/linux/gamedock.service.example /etc/systemd/system/gamedock.service
sudo systemctl daemon-reload
sudo systemctl enable --now gamedock.service
sudo systemctl status gamedock.service

Before using the example service file, replace TWOJ_UZYTKOWNIK with the correct Linux account name.

You can view the logs with:

journalctl -u gamedock.service -f

API

Main endpoints:

Method

Endpoint

Description

GET

/api/machine/status

computer status and detected operating system

GET

/api/machine/metrics

computer telemetry

POST

/api/machine/wake

power on the computer

POST

/api/machine/restart

restart the current operating system

POST

/api/machine/shutdown

shut down the current operating system

POST

/api/machine/systems/{systemId}/boot

set the GRUB entry and restart

POST

/api/machine/systems/{systemId}/wake-boot

automatically power on and boot the selected system

GET

/api/machine/boot-sequence

current boot sequence status

GET

/api/machine/graphical-interface

graphical interface status

POST

/api/machine/graphical-interface/toggle

toggle the graphical interface

GET

/api/gpio/buttons

GPIO button service status

GET

/api/nas/status

NAS status

GET

/api/nas/metrics

NAS telemetry

POST

/api/nas/wake

Wake-on-LAN for the NAS

POST

/api/nas/restart

restart the NAS

POST

/api/nas/shutdown

shut down the NAS

Control operations are rate-limited to 8 requests per minute per IP address.

Security

This version of the application does not implement user authentication or authorization, and the default Urls setting listens on all interfaces (0.0.0.0). The panel can perform power operations and administrative commands on other devices.

Recommended precautions:

do not expose port 5080 directly to the Internet,

use a trusted LAN, VPN/Tailscale, or an authenticated reverse proxy,

restrict access with a firewall,

use dedicated SSH accounts with minimal privileges,

prefer SSH keys or secure secret management instead of storing passwords in the repository,

do not commit real passwords or other secrets to appsettings.json.

NuGet Packages

The project uses, among others:

SSH.NET — SSH connections and command execution,

System.Device.Gpio — GPIO support.

Exact package versions are defined in StartPanel/StartPanel.csproj.

License

The current package does not contain a LICENSE file. If you plan to publish the repository publicly, add an appropriate license first.
