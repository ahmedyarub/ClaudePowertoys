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

- **Remove sidebar resize handle** (on by default): covers the sidebar's resize handle with an
  invisible window that takes the mouse, so it can't be dragged and can't close the floating
  sidebar. It follows the handle: the left edge while the sidebar is collapsed, the sidebar's
  right edge while it is docked. The app itself still has the handle; it just never sees the mouse.

- **Ctrl+Tab session switcher** (on by default): with Claude in front, hold Ctrl and press Tab
  to list the recently active Code sessions, most recently used first, with the previous one
  selected. Tab / Shift+Tab move the selection, Esc cancels, releasing Ctrl switches. The app's
  own Ctrl+Tab (Go > Next Session) is suppressed while this is on. "Recently active" means activity
  in the last 8 hours (`RecentSessionHours` in `settings.json`); the session on screen is always
  listed first. Sessions are opened through the app's `claude://claude.ai/epitaxy/<id>` links.

- **Ctrl+Enter sends now** (on by default): Ctrl+Enter in Claude sends your message right away,
  stopping the current reply, by pressing the app's own "Send now" on the queued message. With
  the prompt box empty it sends a message that is already queued. The key still reaches the app
  first, so when Claude is idle it is an ordinary send. Ctrl+Enter inside another text field
  (the Files editor, a dialog) is left alone.

- **Conversation outline button** (on by default): a round ☰ button just right of the
  conversation column. Click it for a list of the prompts you typed in the session on screen
  (read from its Claude Code transcript, `~/.claude/projects/<project>/<session>.jsonl`, including
  prompts sent while Claude was working); pick one to scroll the conversation to it. Prompts that
  aren't loaded are reached by pressing the app's "Load earlier/later messages" buttons.
  Right-click it to **copy Claude's last answer as Markdown** or **export the conversation** to a
  `.md` file (your prompts and Claude's text, without tool calls or thinking), both read from the
  transcript, so unloaded parts are included.

- **Session status**: the Ctrl+Tab list shows each session's state (needs you / new reply /
  working / idle) and context size, and the tray icon shows a red badge with the number of
  sessions waiting for you (Claude asked a question or wants a plan approved, or finished a turn
  you haven't seen), naming them in its tooltip. Left-click the icon to open the first one.
  Working sessions show how long the current turn has run ("working 12 min"), in the list and in
  the tooltip.
  States come from each session's transcript, read as it grows. Permission prompts aren't in the
  transcript, so a session waiting on one shows as working.
- **Ctrl+Up/Down jumps between prompts** (on by default): in Claude, Ctrl+Up / Ctrl+Down scroll
  the conversation to your previous / next prompt, loading earlier messages when needed. Left
  alone in other text fields (terminal, Files editor); in the prompt box they jump instead of
  moving the cursor by paragraph.
- **Snippets** (on by default): reusable prompts in `%APPDATA%\ClaudePowertoys\snippets.json`
  (`[{ "name": ..., "text": ... }]`, created with two examples). Ctrl+Shift+Space in Claude opens a
  picker above the prompt box (1-9 pick); the tray's Snippets submenu lists them too and has
  Edit snippets…. A snippet is typed at the cursor, line breaks as Shift+Enter, so nothing is sent
  and the clipboard is left alone.
- **Win+Alt+C: sessions waiting for you** (on by default): from any app, opens Claude Desktop's
  own "Sessions Waiting for You" (`claude://code/needs-input`). Change the combination with
  `NeedsInputHotkeyKeys` in `settings.json` (Win+Shift+C is taken on this machine); the tray menu
  says so when another program owns the combination.

- **Win+Alt+N: new session in a recent folder** (on by default): from any app, a menu at the
  mouse of the folders your unarchived sessions worked in (most recent first, 1-9 pick, 0 for no
  folder) starts a new Code session there through `claude://code/new?folder=`, the same link the
  app's own jump list uses. `NewSessionHotkeyKeys` in `settings.json` changes the keys.
- **Start with Windows** (off by default): adds Powertoys to your user's startup programs
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), pointing at the running exe.

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
