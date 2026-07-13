# PowerShellPlus

PowerShellPlus is a Windows desktop workspace for people who regularly have more than one PowerShell session open. It keeps those terminals together in one window, lets you arrange them into resizable rows or columns, saves commands you use often, and can run commands on a schedule without turning the terminal into a read-only log viewer.

The main application is native WPF on .NET 8. Its terminals are backed by Windows ConPTY and Microsoft TerminalControl, so they remain real interactive terminals: prompts, colors, keyboard input, full-screen console programs, and tools such as Codex continue to work normally.

## What it can do

- Run several interactive PowerShell terminals in one window.
- Arrange terminals as a grid, rows, columns, or a focused pane.
- Resize panes by dragging the dividers; nearby panes adjust automatically.
- Save session names, working directories, and shell commands.
- Keep a reusable command library and run a command in one or every terminal.
- Schedule commands by interval, once at an exact date and time, or every day at an exact time.
- Test an automation without moving its next scheduled run.
- See a live countdown until an automation runs.
- Right-click a session, command, or automation card for its available actions.
- Inherit the font and color scheme from your Windows Terminal profile, with optional overrides in Settings.
- Keep the real PowerShell, Codex, SSH, job, and native-program processes alive when the window is closed.
- Recover pane output after a full app or Windows restart and resume Codex only when that pane was actually running Codex.

PowerShellPlus stores its workspace locally under `%APPDATA%\PowerShellPlus`. It does not require an account, an API key, or a cloud service.

## Installation for beginners

There is no traditional installer yet. The included build script creates a ready-to-run copy of the app for you. You only need to do these steps once.

### 1. Check your computer

You need:

- A 64-bit PC running Windows 10 version 2004 or newer, or Windows 11.
- An internet connection for the first build.
- Windows PowerShell, which is already included with Windows.

Windows Terminal is recommended because PowerShellPlus can inherit its appearance, but it is not required.

### 2. Install Git

1. Download **Git for Windows** from [git-scm.com/download/win](https://git-scm.com/download/win).
2. Run the installer.
3. The default choices are fine; keep clicking **Next**, then click **Install**.
4. When it finishes, open the Start menu, type **PowerShell**, and open **Windows PowerShell**.

### 3. Download PowerShellPlus

Copy the following commands, paste them into PowerShell, and press Enter:

```powershell
cd $HOME
git clone https://github.com/jordanw0204-rgb/PowerShellPlus.git
cd PowerShellPlus
```

You should now see `PowerShellPlus` at the left side of your PowerShell prompt. If Git says the destination already exists, skip the `git clone` command and run `cd $HOME\PowerShellPlus` instead.

### 4. Build the app

Run this command from inside the PowerShellPlus folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The first build can take a few minutes. The script downloads a private, project-local copy of the .NET 8 SDK when needed, restores the required packages, compiles the app, runs its terminal and layout tests, and creates the final application. It does not replace or reconfigure the system-wide version of .NET.

When the build succeeds, the last lines will show:

```text
PowerShellPlus Native build complete.
Application: ...\PowerShellPlus\dist\PowerShellPlus.exe
```

### 5. Start PowerShellPlus

Run:

```powershell
.\dist\PowerShellPlus.exe
```

For easier access later:

1. Open the `PowerShellPlus\dist` folder in File Explorer.
2. Right-click `PowerShellPlus.exe`.
3. Choose **Show more options**, then **Send to → Desktop (create shortcut)**.

The app is currently unsigned. Windows SmartScreen may show a warning the first time you open a build you created yourself. Read the dialog carefully; if it identifies the `PowerShellPlus.exe` you just built, choose **More info → Run anyway**. You should never disable SmartScreen globally.

## Your first five minutes

1. Click the **+** beside Sessions to create another terminal.
2. Give the session a useful name and choose its starting folder.
3. Use the layout buttons above the terminals to switch between grid, rows, columns, and focus mode.
4. Drag a divider between terminals to resize them.
5. Open Commands to save something you type often, or Automate to create a scheduled command.

Double-clicking a saved command runs it in the selected terminal. Double-clicking an automation runs it immediately. You can also click the `⋯` button or right-click any card to see every action available for that item.

Scheduled commands are real PowerShell commands. Test a new automation first, especially if it changes files, installs software, or stops processes.

## How session recovery works

PowerShellPlus uses two kinds of recovery because a live Windows process cannot be serialized and recreated perfectly.

When you click the window's close button, PowerShellPlus hides in the Windows system tray by default. The application and its ConPTY processes remain alive, so PowerShell variables, running commands, Codex chats, SSH connections, background jobs, and interactive programs remain exactly where you left them. Double-click the PowerShellPlus tray icon—or start PowerShellPlus again—to bring the existing window back. A second launch activates the already-running instance instead of creating duplicate terminals.

To genuinely exit, right-click the tray icon and choose **Quit and close sessions**, or use **Quit PowerShellPlus and close all sessions** in Settings.

If PowerShellPlus is terminated, crashes, updates, or Windows restarts, the original processes cannot remain alive. In that case PowerShellPlus:

1. Recreates the saved panes and layout.
2. Starts normal shells in their configured working directory and restored Codex panes in the directory where that Codex chat actually started.
3. Makes the previous terminal output available from the history icon in the pane header.
4. Installs a small pane-local PowerShell wrapper around the existing `codex` command. Before Codex starts, the wrapper records that pane's real working directory and launch time; it also records when Codex exits normally.
5. Correlates that pane marker with Codex's local rollout metadata and saves the durable thread ID plus that thread's most recently applied model. After an app crash, update, or Windows restart, recovery calls `codex resume <thread-id> --model <saved-model>` directly rather than inheriting whichever chat or model another terminal changed most recently.

PowerShellPlus never decides that a pane is Codex merely because the word “Codex” appeared in its output. Detection comes from the pane's live process tree and its own launch marker. Model selection comes from Codex's structured `thread_settings_applied` and `turn_context` records, not terminal text. A normal PowerShell pane will therefore never be changed into a Codex session during recovery. These marker files contain session identifiers, model names, timestamps, and local folder paths—not chat contents or API credentials—and remain under `%APPDATA%\PowerShellPlus\session-recovery`.

Recovery options are available under **Settings → Session recovery**. Saved terminal output is limited to the most recent 500,000 characters per pane and remains local in the PowerShellPlus data folder. Terminal output can contain private commands or tokens, so transcript saving can be disabled independently.

## Updating later

Open PowerShell in the project folder and run:

```powershell
git pull
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Then start the refreshed copy from `dist\PowerShellPlus.exe`.

## Troubleshooting

### The build says a file in `dist` is being used

Close PowerShellPlus, wait a few seconds, and run the build command again. Windows cannot replace the deployed executable while it is open.

### Git is not recognized

Close PowerShell, reopen it after installing Git, and retry. If that still fails, restart Windows so the installer can finish updating your PATH.

### Package restore or SDK download fails

Check your internet connection and retry. Corporate networks, school networks, VPNs, and antivirus products can block NuGet or Microsoft's SDK download service.

### The terminal appearance is unexpected

Open Settings inside PowerShellPlus. Blank appearance fields inherit from your Windows Terminal default profile; entering a font or size overrides that value for PowerShellPlus.

### I want a completely fresh workspace

PowerShellPlus saves user-created sessions, commands, automations, and preferences in:

```text
%APPDATA%\PowerShellPlus
```

Close the app and rename that folder to `PowerShellPlus-backup`. The next launch creates a fresh workspace, while the renamed folder preserves your old data in case you want it back.

## Building and testing for development

The normal build command is also the complete release gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

It performs a Release build, tests interactive ConPTY input/output, checks multi-pane resizing and layouts, validates automation timing and countdown formatting, verifies live hide/restore process identity, checks Codex-only recovery with exact launch-marker, thread-ID, and model binding, tests launching and detecting Codex inside the terminal, publishes a self-contained Windows x64 build, repeats the native tests against the published build, and produces:

- `dist\PowerShellPlus.exe` and its runtime files
- `PowerShellPlus-win-x64.zip`

For a faster compile/publish cycle without the runtime gates:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests
```

The repository also retains the earlier WinForms and Electron implementations for reference. Active development is centered on `native\PowerShellPlus.Native`.

## Project layout

```text
native/PowerShellPlus.Native/   Current WPF application
native/NuGet.config             Native package source configuration
src/PowerShellPlus/             Earlier WinForms implementation
electron/ and renderer/         Electron fallback implementation
scripts/                        Smoke tests and deployment helpers
build.ps1                       Native build, test, publish, and package pipeline
```

## License

PowerShellPlus is available under the [MIT License](LICENSE).
