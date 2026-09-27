# Juicy Folder Meter

A free Windows 11 utility that shows folder-size status without replacing normal folder icons. The tray application calculates folder sizes and a small native Explorer shell extension can display orange/red status markers where Windows has an available icon-overlay slot.

## Screenshots

![Juicy Folder Meter](Screenshot%20-%20typical%20window.png)

![Options](Screenshot%20-%20options%20window.png)

## What it does

- Calculates recursive sizes for local folders.
- Uses configurable orange/red thresholds.
- Can follow folders exposed by open Explorer windows automatically.
- Keeps filesystem scanning out of Explorer; the Explorer extension only reads a bounded shared-memory cache.
- Does not disable, rename, or reorder other applications' overlay handlers.
- No ads, accounts, analytics, telemetry, or cloud service.

Windows has a small system-wide limit on icon-overlay handlers, so another installed application can prevent the visual marker from appearing even when Juicy Folder Meter itself is working. The size table in the tray application is independent of that Windows limitation.

## Requirements

- Windows 11 x64
- For building: .NET 8 SDK, Visual Studio C++ Build Tools with Windows SDK
- WiX Toolset 6 is installed automatically by the Store MSI build script when needed

The Store build publishes the .NET application self-contained, so end users do not need to install the .NET Desktop Runtime separately.

## Build

Run:

```powershell
./BUILD-STORE-MSI.ps1 -Version 1.1.0
