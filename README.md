# PowerShellPlus

PowerShellPlus is a Windows desktop workspace for people who regularly have more than one PowerShell session open. It keeps those terminals together in one window, lets you arrange them into resizable rows or columns, saves commands you use often, and can run commands on a schedule without turning the terminal into a read-only log viewer.

The main application is native WPF on .NET 8. Its terminals are backed by Windows ConPTY and Microsoft TerminalControl, so they remain real interactive terminals: prompts, colors, keyboard input, full-screen console programs, and tools such as Codex continue to work normally.

## What it can do

- Run several interactive PowerShell terminals in one window.
- Activate a session by clicking anywhere inside its terminal pane, including the native terminal surface.
- Use a dedicated PowerShellPlus icon in the executable, taskbar, window switcher, and notification tray.
- Arrange terminals as a grid, rows, columns, or a focused pane.
- Resize panes by dragging the dividers; nearby panes adjust automatically.
- Collapse the workspace sidebar to give the terminal grid its space, then expand it without disturbing pane proportions.
- Save session names, working directories, and shell commands.
- Keep a reusable command library and mark selected entries for quick access inside every terminal.
- Use a compact, auto-growing command bar and saved command queue independently in each terminal pane.
- Paste clipboard text with `Ctrl+V` even inside Codex, while preserving Codex's image-only paste path when the clipboard has no text.
- Keep the layout controls centered in the title bar as the window resizes, with one-click Windows Terminal access beside the window controls.
- Schedule commands by interval, once at an exact date and time, or every day at an exact time.
- Test an automation without moving its next scheduled run.
- See a live countdown until an automation runs.
- Right-click a session, command, or automation card for its available actions.
- Inherit the font and color scheme from your Windows Terminal profile, with optional overrides in Settings.
- Keep the real PowerShell, Codex, SSH, job, and native-program processes alive when the window is closed.
- Recover pane output after a full app or Windows restart, including the exact Codex thread selected through an in-session `/resume`, its model and permission level, plus validated SSH reconnection details and a running remote Hermes session with that session's own model.
- Drag an existing Windows Terminal window onto PowerShellPlus to recreate every tab as a named native session, retain its scrollback, and resume verified Codex threads with their model and complete `/permissions` selection.
- Click **>_** in a pane header to move that session back into a new Windows Terminal window through a verified two-phase handoff. Exact Codex threads, models, permission settings, working folders, transcripts, and queued commands are retained wherever the underlying tools expose them safely.
- Click the globe in the title bar to mirror every live session to a phone or browser in **LAN** mode or switch to browser-only **GLOBAL** mode for access from anywhere, with saved-device pairing, a responsive xterm.js view, and optional remote typing.

PowerShellPlus stores its workspace locally under `%APPDATA%\PowerShellPlus`. The desktop app and LAN mode do not require an account, API key, or cloud service. Optional Global mode uses a Tailscale account and Funnel connector on the computer; the phone needs only a normal web browser.

## Installation for beginners

There is no traditional installer yet. The included build script creates a ready-to-run copy of the app for you. You only need to do these steps once.

### 1. Check your computer

You need:

- A 64-bit PC running Windows 10 version 2004 or newer, or Windows 11.
- An internet connection for the first build.
- Windows PowerShell, which is already included with Windows.
- The current [Node.js LTS](https://nodejs.org/) release, used once to restore the pinned xterm.js browser assets.

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

Each terminal has its own command bar along its bottom edge. Press **Enter** to run its current command, or press **Ctrl+Enter** to place it at the end of that pane's queue. Click **Queue** to open the numbered, scrollable queue and choose any pending command; its badge shows how many commands are waiting. After a command runs, the next queued command is promoted into the input without running automatically. Press **Up** or **Down** while the input is focused to browse the pending queue. Long input wraps and expands upward to six visible lines while staying aligned to the top. Queue contents and the expanded or collapsed state are saved with the session. Hover over the thin strip at the bottom center of a pane to reveal the collapse arrow.

Use the slim arrow on the right edge of the navigation rail to collapse or restore the workspace sidebar. The terminal panes resize immediately into the available width, and the sidebar state is saved with the workspace.

### View your sessions from a phone — LAN or Global

Click the **globe** beside the Windows Terminal button in the title bar. **LAN** starts the embedded website only on this PC's active private IPv4 addresses. The dialog names every adapter, places the real Wi-Fi/Ethernet route first as **Recommended**, and labels virtual routes so you know which address reaches your device. Open that address, name the browser, and enter the eight-digit pairing code.

Switch the same dialog to **GLOBAL** to use the site from cellular data, another Wi-Fi network, or anywhere else. Global mode requires the current Tailscale client on this Windows PC and a one-time Tailscale sign-in/Funnel approval. If Tailscale is missing, the themed setup prompt can download the newest stable Windows installer from Tailscale's official package server and open it for you. If Windows reports `NeedsLogin`, PowerShellPlus explains that Tailscale is a system-tray client, obtains a one-time address from the installed CLI, accepts only an exact `https://login.tailscale.com` origin, opens the browser sign-in, waits for completion, and retries Global mode automatically; identity-provider credentials never pass through PowerShellPlus. The same validated browser flow handles Tailscale's first-use Funnel approval, and the public endpoint uses standard HTTPS port 443 so its address works without a special port suffix. PowerShellPlus does not call the address ready merely because the host PC can reach it through private MagicDNS: it independently resolves the hostname through public DNS-over-HTTPS, rejects loopback/private/CGNAT answers, connects directly to a returned public Funnel relay, and requires a valid hostname-bound TLS response from the PowerShellPlus web app. The mapping stays alive through Tailscale's documented first-use DNS propagation window while that check runs. Installer setup resolves through Tailscale's official Windows package manifest (with the stable package page and a release-pinned official package as availability fallbacks), retries brief installer-download failures, accepts only the official HTTPS package path, limits the download size, and requires both valid Windows Authenticode trust and the exact `Tailscale Inc.` publisher before anything can run. Windows still shows its normal UAC/installer confirmation. It does **not** require Tailscale, a VPN, an account, or any PowerShellPlus software on the phone: open the generated `https://…ts.net` address directly in Safari, Chrome, or another modern browser and enter the 12-digit one-time code. Tailscale Funnel provides the public DNS name, valid HTTPS certificate, encrypted relay, and home-IP hiding; PowerShellPlus remains bound only to `127.0.0.1` and never opens or forwards a router port.

If Tailscale is installed but disconnected, choosing **GLOBAL** connects it with a no-reset `tailscale up` command before creating the Funnel. PowerShellPlus records ownership of that connection and runs `tailscale down` when sharing stops or startup fails. If Tailscale was already connected—or another app or user connected it—PowerShellPlus leaves that connection running and removes only its own Funnel. It never stops the Windows Tailscale service or resets existing DNS, route, exit-node, SSH, or unattended-mode settings.

In either mode, every current terminal appears with live VT colors, scrollback, session names, grid/focus layouts, and font controls. The web renderer uses each native pane's real row/column grid before decoding its VT output, then scales the visual cells to the available card without resizing the shared ConPTY. Sessions added or removed on the PC are synchronized automatically.

Remote access is read-only by default. Enable **Allow paired devices to type in terminals** only when you need it. Remote keystrokes then go directly to the selected existing ConPTY, so PowerShell, Codex, SSH, and native programs keep exactly the permissions they already have; the browser does not create or resume a second shell. The remote view never resizes the shared ConPTY, so its layout cannot disturb the desktop terminal. Rotating a phone, showing its software keyboard, or resizing a desktop browser automatically re-fits the terminal cards to the visible viewport.

Every remote session also has its own growing command line. **Enter** sends, **Shift+Enter** inserts a line, and **Ctrl+Enter** (or **Cmd+Enter**) adds the command to that session's persisted queue. **Queue** opens and selects pending commands, including a touch-friendly add action, while the lightning button opens the same Quick Access commands configured in the desktop app.

LAN mode uses local HTTP, so use it only on trusted Private Wi-Fi/Ethernet. Do not use LAN mode on hotel, airport, guest, school, or other untrusted networks; if Windows Firewall asks, allow **Private networks** only. Requests must come from the same IPv4 subnet and direct public/router-forwarded traffic is rejected. Never port-forward the LAN Remote port.

Global mode deliberately exposes only the authenticated web application through Tailscale Funnel. The local Kestrel listener accepts loopback traffic only; the public host and HTTPS WebSocket origin must match exactly; HSTS and Secure/HttpOnly/SameSite cookies are enforced; the global 12-digit code is throttled to five failed attempts per minute across the entire endpoint; and the 256-bit saved device secret is stored only as a SHA-256 hash. Saved devices survive application and sharing restarts, appear in the Remote Access dialog with connection and last-seen details, and can be revoked immediately. Remote typing returns to read-only whenever Global mode starts. Funnel runs in the foreground without `--bg`, is removed with its exact scoped command when sharing stops, and is killed during shutdown so an abandoned public route is not left behind. Tailscale Funnel is currently a Tailscale beta and applies its published bandwidth limits.

Hold **Shift** while pressing **Enter** or clicking the send button to send the command to every open terminal. The send icon turns purple and changes to a double arrow before anything is sent, so the wider action is visible. You can disable this shortcut or change its modifier to **Alt** under **Settings → Behavior**. `Ctrl+Enter` is always reserved for adding the current command to its pane's queue.

Funnel cleanup is idempotent. If Tailscale reports that the handler is already absent, PowerShellPlus re-reads Funnel status and accepts that result only after HTTPS port 443 is verified clear.

To put a saved command in the lightning-bolt menu, open **Commands**, edit that command, and enable **Show in terminal quick access**. Selecting it from a terminal's quick-access menu fills that pane's input so you can review, edit, queue, or run it.

Scheduled commands are real PowerShell commands. Test a new automation first, especially if it changes files, installs software, or stops processes.

### Import an existing Windows Terminal window

Drag a Windows Terminal window by its title bar over PowerShellPlus and hold it there briefly. When the overlay says **Release to import Windows Terminal**, release the mouse. PowerShellPlus reads the tab names and scrollback without closing the source, then shows one review row per tab.

For a Codex tab, select the matching active Codex thread if PowerShellPlus could not choose it unambiguously. The review shows the working directory, model, permission profile or legacy sandbox, approval policy, and approval reviewer. PowerShellPlus will not resume a selected Codex thread unless all required permission metadata is valid. Choose **Close source & import** to finish. Only then does PowerShellPlus close that Windows Terminal window and create the replacement sessions, preventing the same Codex thread from running in two terminals simultaneously.

Each imported session uses the Windows Terminal tab's name. A transient Codex activity spinner is removed from the beginning of the name. Split panes inside one Windows Terminal tab are captured together as previous output, but the initial importer creates one PowerShellPlus session per tab.

This is a recreation, not a transfer of the original PowerShell process. Variables, loaded modules, jobs, SSH connections, and other in-memory state end when the source window closes. The review dialog calls this out before making any change.

### Move a session back to Windows Terminal

Click **>_** in a session pane header. PowerShellPlus verifies Windows Terminal and PowerShell first. If Codex is running, it also requires a live, unambiguous top-level thread ID and a complete validated permission state; if another non-Codex child program is running, the handoff is blocked until that program exits. The confirmation shows the exact thread, model, permission selection, working folder, transcript location, and queued-command copy that will be used.

After confirmation, PowerShellPlus opens a new Windows Terminal window whose PowerShell bootstrap writes a proof-of-life marker and waits. The source ConPTY is stopped and removed only after that marker identifies a live PowerShell process. PowerShellPlus then atomically releases the new shell, which runs `codex resume <thread-id>` with the captured model, approval policy, reviewer, and permission profile or legacy sandbox. If startup or verification fails, the source pane remains untouched; this avoids running two copies of one Codex thread.

The local transfer bundle is kept under `%APPDATA%\PowerShellPlus\session-handoffs` for 30 days and includes a plain-text terminal transcript plus any queued commands. As with import, this is a controlled reconstruction because Windows cannot move an existing ConPTY client process into a different terminal host. PowerShell variables, functions, loaded modules, jobs, SSH connections, live child programs, and process memory cannot be transferred. An active Codex turn or tool command is interrupted when its old process stops, but its durable conversation continues from the verified thread.

## How session recovery works

PowerShellPlus uses two kinds of recovery because a live Windows process cannot be serialized and recreated perfectly.

When you click the window's close button, PowerShellPlus hides in the Windows system tray by default. The application and its ConPTY processes remain alive, so PowerShell variables, running commands, Codex chats, SSH connections, background jobs, and interactive programs remain exactly where you left them. Double-click the PowerShellPlus tray icon—or start PowerShellPlus again—to bring the existing window back. A second launch activates the already-running instance instead of creating duplicate terminals.

To genuinely exit, right-click the tray icon and choose **Quit and close sessions**, or use **Quit PowerShellPlus and close all sessions** in Settings.

If PowerShellPlus is terminated, crashes, updates, or Windows restarts, the original processes cannot remain alive. In that case PowerShellPlus:

1. Recreates the saved panes and layout.
2. Starts normal shells in their configured working directory, restored Codex panes where that chat started, and interrupted SSH panes against their saved host/config alias.
3. Makes the previous terminal output available from the history icon in the pane header.
4. Installs small pane-local PowerShell wrappers around the existing `codex` and Windows OpenSSH `ssh.exe` commands. They record launch/lifecycle metadata without replacing either executable.
5. Correlates the pane's live Codex process with Codex's local activity and rollout metadata, including rollout files Codex still has open for writing, then saves the durable thread ID plus that thread's most recently applied model and `/permissions` level. It can resolve the process through either the terminal pane or its launch marker, with launch-time correlation as a final fallback. If `/resume` switches threads inside Codex, the pane binding follows the selected top-level thread while ignoring background subagent activity. After an app crash, update, or Windows restart, recovery calls `codex resume <thread-id>` with the saved model, approval policy, approval reviewer, and either the saved permission profile or legacy sandbox. Permission-profile resumes use `default_permissions` and deliberately do not pass `--sandbox`, because Codex treats the newer permission profiles and legacy sandbox settings as mutually exclusive.
6. Confirms that the pane still owns a live `ssh.exe` process, then saves only a validated connection recipe: destination or SSH config alias plus supported port, user, identity-file, config-file, jump-host, address-family, compression, agent-forwarding, and X11 options. On recovery it reconnects with a forced interactive PTY. Recovery adds bounded OpenSSH connection attempts, handshake timeout, and encrypted server-alive checks so an unreachable or overloaded SSH server cannot leave a permanently blank pane. The restore status and any connection error remain visible, the local PowerShell prompt becomes interactive after the bounded attempt, and the pane restart button retries the same saved recovery. A connection failure keeps the previous transcript, SSH recipe, exact Hermes ID, and validated per-session Hermes model for the next retry instead of overwriting them with the failed pane. PowerShellPlus recognizes Hermes' resume banner plus full and compact status bars, follows in-session `/model` changes, and supplies the saved model as a per-run `--model` override when it resumes the exact session. If no exact ID is available, it uses Hermes' own `--continue` lookup with the saved pane model. A normal Hermes exit is recognized and is not relaunched.

SSH passwords, key passphrases, private-key contents, arbitrary `-o` directives, forwarding commands, and remote shell commands are never added to recovery metadata. The only accepted `-o` values are bounded `ConnectionAttempts`, `ConnectTimeout`, `ServerAliveInterval`, and `ServerAliveCountMax` reliability settings; injected defaults are not persisted as connection identity. Authentication remains OpenSSH's job through `%USERPROFILE%\.ssh\config`, `ssh-agent`, key files, and `known_hosts`, so host-key checking is not weakened. If authentication still needs a password or key passphrase after restart, OpenSSH prompts in the restored pane as usual. Unsupported or malformed SSH arguments fail closed: the shell opens normally instead of replaying them.

For PowerShellPlus-owned panes, the app never decides that a pane is Codex merely because the word “Codex” appeared in its output. Detection comes from the pane's live process tree and its own launch marker. During an external Windows Terminal import, terminal accessibility text identifies which review rows look like Codex, but a resume is enabled only after that row is bound to a live top-level Codex thread from structured activity metadata. Model and permission recovery always comes from Codex's structured session records, not terminal text. Only validated permission-profile names, legacy sandbox modes, approval policies, and approval reviewers are accepted. An incomplete external match cannot auto-resume. These marker files contain session identifiers, model names, permission mode names, timestamps, and local folder paths—not chat contents or API credentials—and remain under `%APPDATA%\PowerShellPlus\session-recovery`.

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

It performs a Release build, tests interactive ConPTY input/output, checks multi-pane resizing and responsive title controls, exercises pane-local command bars, persisted and scrollable queues, queue navigation, quick-access filtering, configurable send-to-all routing, and real fan-out to every live pane, validates automation timing and countdown formatting, verifies live hide/restore process identity, checks Codex-only recovery with exact launch-marker, thread-ID, model, and permission binding, tests launching and detecting Codex inside the terminal, validates the Windows Terminal handoff's structured resume arguments, proof-of-life wait, atomic release, transcript/queue preservation, unsafe-permission rejection, and cancellation path, and runs an embedded Remote Access integration test covering persistent hash-only pairing across a fresh server instance, live-device revocation, adapter metadata, unauthenticated rejection, WebSocket origin validation, dimension-faithful session snapshots and output, responsive web controls, remote command queues, direct input, live VT output, LAN subnet enforcement, Global loopback isolation, exact HTTPS host/origin validation, HSTS, Secure cookies, global pairing throttling, scoped Funnel arguments, Tailscale installer URL/launch boundaries, unsigned-installer rejection, and clean shutdown. It publishes a self-contained Windows x64 build, repeats the native tests against the published build, and produces:

- `dist\PowerShellPlus.exe` and its runtime files
- `PowerShellPlus-win-x64.zip`

For a faster compile/publish cycle without the runtime gates:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests
```

If a deployed copy is currently running and its live sessions must not be interrupted, use `-StageOnly`. The tested app remains in `release-native` and is packaged as `PowerShellPlus-win-x64-staged.zip`; the active `dist` folder is left untouched.

The repository also retains the earlier WinForms and Electron implementations for reference. Active development is centered on `native\PowerShellPlus.Native`.

## Project layout

```text
native/PowerShellPlus.Native/   Current WPF application
native/PowerShellPlus.Native/RemoteWeb/  Embedded phone/browser terminal client
native/PowerShellPlus.Native/TailscaleFunnelManager.cs  Browser-only Global HTTPS tunnel lifecycle
native/PowerShellPlus.Native/TailscaleInstaller.cs  Official installer download and publisher verification
native/PowerShellPlus.Native/PowerShellPlusDialog.xaml  Shared themed app prompt and confirmation surface
native/NuGet.config             Native package source configuration
src/PowerShellPlus/             Earlier WinForms implementation
electron/ and renderer/         Electron fallback implementation
scripts/                        Smoke tests and deployment helpers
build.ps1                       Native build, test, publish, and package pipeline
```

## License

PowerShellPlus is available under the [MIT License](LICENSE).
