# Claude Powertoys

A tray app that adds small tweaks to the Claude desktop app without modifying it. It drives the
app's own controls through Windows UI Automation.

Right-click the **PT** icon in the notification area to use it.

## Features

- **Auto-hide sidebar**: touch the window's left edge (within 12 px) and the sidebar slides
  out *over* the main panel, which keeps its full width. It closes by itself once the mouse leaves
  it. This is the app's own floating sidebar (the one that appears when you hover the collapsed
  Show sidebar button); Powertoys just triggers it from the edge, without moving your cursor.
  Clicking the app's Show sidebar button docks (pins) the sidebar as usual, and auto-hide leaves
  it alone until you hide it again.

## Build and run

```
dotnet publish -c Release -o publish
publish\ClaudePowertoys.exe
```

Requires the .NET 10 Desktop Runtime.

Settings and the log live in `%APPDATA%\ClaudePowertoys\` (`settings.json`, `log.txt`).

## Limits

- Finds the sidebar button by its English name (`Show sidebar` / `Hide sidebar`), so it only works
  with the app's UI language set to English, and may need updating if an app release renames it
  or changes how the floating sidebar opens.
