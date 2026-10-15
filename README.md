# Shake to Find (Windows 11)

Shake your mouse and the cursor grows, then shrinks back when you stop, like on macOS.
Runs from the system tray. Right-click the tray icon for "Start with Windows" and "Exit".

## Build

Install the .NET 8 SDK (or newer): https://dotnet.microsoft.com/download

Then, in this folder:

    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

The exe lands in `bin\Release\net8.0-windows\win-x64\publish\ShakeToFind.exe`.
That version is tiny but needs the .NET 8 Desktop Runtime installed.

For one exe that runs anywhere (about 70 MB):

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

For ARM Windows, use `-r win-arm64`.

## Tuning

Edit the `Settings` class at the top of `Program.cs`:
- `MaxScale`: how big the cursor gets
- `ReversalsNeeded` / `MinSegmentPx`: how vigorous a shake must be
- `HoldMs`: how long it stays big after you stop
- `GrowTauMs` / `ShrinkTauMs`: animation speed

## How it works

A low-level mouse hook watches for rapid direction reversals. On a shake, a transparent,
click-through, always-on-top layered window draws the current cursor image scaled up around
its hotspot, so it grows directly out of the real cursor. The window is hidden when idle,
so the app uses essentially no CPU between shakes.
