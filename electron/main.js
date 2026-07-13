const { app, BrowserWindow, dialog, ipcMain, shell, nativeTheme } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const pty = require('node-pty');

const sessions = new Map();
let mainWindow;
let quitting = false;
const uiSmokeAtStartup = process.argv.find((arg) => arg.startsWith('--ui-smoke'));
if (uiSmokeAtStartup) {
  const smokeProfile = path.resolve('build', 'ui-smoke-profile');
  fs.rmSync(smokeProfile, { recursive: true, force: true });
  app.setPath('userData', smokeProfile);
}

function shellCandidates() {
  const candidates = [];
  const add = (name, executable) => {
    if (executable && fs.existsSync(executable) && !candidates.some((item) => item.path.toLowerCase() === executable.toLowerCase())) {
      candidates.push({ name, path: executable });
    }
  };

  const programFiles = process.env.ProgramFiles || 'C:\\Program Files';
  const powerShellRoot = path.join(programFiles, 'PowerShell');
  if (fs.existsSync(powerShellRoot)) {
    for (const version of fs.readdirSync(powerShellRoot).sort().reverse()) {
      add(`PowerShell ${version}`, path.join(powerShellRoot, version, 'pwsh.exe'));
    }
  }
  add('Windows PowerShell', path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'));
  return candidates;
}

function normalizeProfile(profile = {}) {
  const shells = shellCandidates();
  const defaultShell = shells[0]?.path || 'powershell.exe';
  const cwd = typeof profile.cwd === 'string' && fs.existsSync(profile.cwd) ? profile.cwd : os.homedir();
  return {
    id: String(profile.id || crypto.randomUUID()),
    name: String(profile.name || 'PowerShell'),
    shell: typeof profile.shell === 'string' && profile.shell ? profile.shell : defaultShell,
    cwd,
    startupCommand: String(profile.startupCommand || ''),
    accent: String(profile.accent || '#a1a1aa')
  };
}

function createPty(sender, rawProfile) {
  const profile = normalizeProfile(rawProfile);
  const existing = sessions.get(profile.id);
  if (existing) {
    try { existing.process.kill(); } catch {}
    sessions.delete(profile.id);
  }

  const process = pty.spawn(profile.shell, ['-NoLogo', '-NoProfile'], {
    name: 'xterm-256color',
    cols: 100,
    rows: 30,
    cwd: profile.cwd,
    env: { ...processEnv(), TERM: 'xterm-256color', COLORTERM: 'truecolor' },
    useConpty: true,
    handleFlowControl: true
  });

  const entry = { process, sender, profile };
  sessions.set(profile.id, entry);
  process.onData((data) => {
    if (!sender.isDestroyed()) sender.send('pty:data', { id: profile.id, data });
  });
  process.onExit(({ exitCode, signal }) => {
    if (sessions.get(profile.id) === entry) sessions.delete(profile.id);
    if (!sender.isDestroyed()) sender.send('pty:exit', { id: profile.id, exitCode, signal });
  });
  if (profile.startupCommand.trim()) setTimeout(() => process.write(`${profile.startupCommand}\r`), 250);
  return { id: profile.id, pid: process.pid, profile };
}

function processEnv() {
  const env = {};
  for (const [key, value] of Object.entries(process.env)) if (typeof value === 'string') env[key] = value;
  if (!env.SystemRoot && process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  return env;
}

function workspacePath() {
  return path.join(app.getPath('userData'), 'workspace-v2.json');
}

function defaultWorkspace() {
  const bestShell = shellCandidates()[0]?.path || 'powershell.exe';
  return {
    version: 2,
    name: 'Main workspace',
    layout: 'grid',
    activeSessionId: null,
    sessions: [{ id: crypto.randomUUID(), name: 'PowerShell', shell: bestShell, cwd: os.homedir(), startupCommand: '', accent: '#a1a1aa', autoStart: true }],
    snippets: [
      { id: crypto.randomUUID(), name: 'Git status', category: 'Development', command: 'git status --short --branch' },
      { id: crypto.randomUUID(), name: 'Top processes', category: 'System', command: 'Get-Process | Sort-Object CPU -Descending | Select-Object -First 15' }
    ],
    automations: [],
    settings: { fontSize: 14, fontFamily: 'Cascadia Mono, Consolas, monospace', opacity: 1 }
  };
}

function readWorkspace(file = workspacePath()) {
  try {
    const value = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (!value || value.version !== 2 || !Array.isArray(value.sessions)) throw new Error('Unsupported workspace');
    return value;
  } catch {
    return defaultWorkspace();
  }
}

function writeWorkspace(value, file = workspacePath()) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(value, null, 2), 'utf8');
  if (fs.existsSync(file)) fs.copyFileSync(file, `${file}.bak`);
  fs.renameSync(temporary, file);
  return file;
}

function createWindow(showWindow = true) {
  nativeTheme.themeSource = 'dark';
  mainWindow = new BrowserWindow({
    width: 1500,
    height: 920,
    minWidth: 980,
    minHeight: 640,
    backgroundColor: '#09090b',
    show: false,
    title: 'PowerShellPlus',
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });

  mainWindow.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));
  if (showWindow) mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('maximize', () => mainWindow.webContents.send('window:state', { maximized: true }));
  mainWindow.on('unmaximize', () => mainWindow.webContents.send('window:state', { maximized: false }));
  mainWindow.on('closed', () => { mainWindow = null; });
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  mainWindow.webContents.on('will-navigate', (event) => event.preventDefault());
  return mainWindow;
}

ipcMain.handle('pty:create', (event, profile) => createPty(event.sender, profile));
ipcMain.on('pty:write', (_event, { id, data }) => {
  const session = sessions.get(String(id));
  if (session && typeof data === 'string') session.process.write(data);
});
ipcMain.on('pty:resize', (_event, { id, cols, rows }) => {
  const session = sessions.get(String(id));
  if (session) {
    const width = Math.max(2, Math.min(1000, Number(cols) || 80));
    const height = Math.max(1, Math.min(500, Number(rows) || 24));
    try { session.process.resize(width, height); } catch {}
  }
});
ipcMain.handle('pty:kill', (_event, id) => {
  const session = sessions.get(String(id));
  if (!session) return false;
  sessions.delete(String(id));
  try { session.process.kill(); } catch {}
  return true;
});
ipcMain.handle('pty:restart', (event, { id, profile }) => {
  const session = sessions.get(String(id));
  if (session) { try { session.process.kill(); } catch {} sessions.delete(String(id)); }
  return createPty(event.sender, { ...profile, id });
});

ipcMain.handle('workspace:load', () => readWorkspace());
ipcMain.handle('workspace:save', (_event, value) => writeWorkspace(value));
ipcMain.handle('workspace:import', async () => {
  const result = await dialog.showOpenDialog(mainWindow, { properties: ['openFile'], filters: [{ name: 'PowerShellPlus workspace', extensions: ['json'] }] });
  return result.canceled ? null : readWorkspace(result.filePaths[0]);
});
ipcMain.handle('workspace:export', async (_event, value) => {
  const result = await dialog.showSaveDialog(mainWindow, { defaultPath: 'PowerShellPlus-workspace.json', filters: [{ name: 'PowerShellPlus workspace', extensions: ['json'] }] });
  return result.canceled ? null : writeWorkspace(value, result.filePath);
});
ipcMain.handle('output:export', async (_event, { name, content }) => {
  const safeName = String(name || 'terminal').replace(/[<>:"/\\|?*]/g, '-');
  const result = await dialog.showSaveDialog(mainWindow, { defaultPath: `${safeName}.txt`, filters: [{ name: 'Text', extensions: ['txt', 'log'] }] });
  if (result.canceled) return null;
  fs.writeFileSync(result.filePath, String(content || ''), 'utf8');
  return result.filePath;
});
ipcMain.handle('dialog:directory', async () => {
  const result = await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory', 'createDirectory'] });
  return result.canceled ? null : result.filePaths[0];
});
ipcMain.handle('shell:candidates', () => shellCandidates());
ipcMain.handle('app:open-user-data', () => shell.openPath(app.getPath('userData')));
ipcMain.on('window:minimize', () => mainWindow?.minimize());
ipcMain.on('window:maximize', () => mainWindow?.isMaximized() ? mainWindow.unmaximize() : mainWindow?.maximize());
ipcMain.on('window:close', () => mainWindow?.close());

async function runPtySmoke(reportFile) {
  const shellPath = shellCandidates()[0]?.path || 'powershell.exe';
  let output = '';
  let resolved = false;
  const terminal = pty.spawn(shellPath, ['-NoLogo', '-NoProfile'], {
    name: 'xterm-256color', cols: 100, rows: 30, cwd: os.homedir(), env: processEnv(), useConpty: true
  });
  const finish = (ok, message) => {
    if (resolved) return;
    resolved = true;
    try { terminal.kill(); } catch {}
    if (reportFile) {
      const absolute = path.resolve(reportFile);
      fs.mkdirSync(path.dirname(absolute), { recursive: true });
      fs.writeFileSync(absolute, `${ok ? 'PASS' : 'FAIL'} ${message}\n\n${output}`, 'utf8');
    }
    app.exit(ok ? 0 : 2);
  };
  terminal.onData((data) => {
    output += data;
    if (output.includes('PSPLUS_CONPTY=True,True')) finish(true, 'ConPTY reports interactive input and output.');
  });
  terminal.onExit(() => finish(false, 'PowerShell exited before confirming ConPTY.'));
  setTimeout(() => terminal.write("Write-Output ('PSPLUS_CONPTY=' + (-not [Console]::IsInputRedirected) + ',' + (-not [Console]::IsOutputRedirected))\r"), 250);
  setTimeout(() => finish(false, 'Timed out waiting for ConPTY verification.'), 12000);
}

async function runUiSmoke(screenshotFile) {
  const output = path.resolve(screenshotFile || path.join('build', 'ui-smoke.png'));
  const report = output.replace(/\.png$/i, '.json');
  try {
    const window = createWindow(false);
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error('Renderer load timed out.')), 15000);
      window.webContents.once('did-finish-load', () => { clearTimeout(timeout); resolve(); });
      window.webContents.once('did-fail-load', (_event, code, description) => { clearTimeout(timeout); reject(new Error(`${code}: ${description}`)); });
    });
    await new Promise((resolve) => setTimeout(resolve, 2600));
    const state = await window.webContents.executeJavaScript(`({
      title: document.title,
      cards: document.querySelectorAll('.terminal-card').length,
      xterms: document.querySelectorAll('.xterm').length,
      status: document.querySelector('#statusMessage')?.textContent,
      background: getComputedStyle(document.documentElement).getPropertyValue('--bg').trim(),
      size: [document.documentElement.clientWidth, document.documentElement.clientHeight]
    })`);
    const image = await window.webContents.capturePage();
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, image.toPNG());
    const ok = state.cards > 0 && state.xterms === state.cards && state.status && !state.status.toLowerCase().includes('failed') && state.background === '#09090b';
    fs.writeFileSync(report, JSON.stringify({ ok, ...state, screenshot: output }, null, 2));
    app.exit(ok ? 0 : 3);
  } catch (error) {
    fs.mkdirSync(path.dirname(report), { recursive: true });
    fs.writeFileSync(report, JSON.stringify({ ok: false, error: error.stack || error.message }, null, 2));
    app.exit(4);
  }
}

app.on('before-quit', () => {
  quitting = true;
  for (const session of sessions.values()) { try { session.process.kill(); } catch {} }
  sessions.clear();
});
app.on('window-all-closed', () => { if (process.platform !== 'darwin' || quitting) app.quit(); });
app.on('activate', () => { if (!BrowserWindow.getAllWindows().length) createWindow(); });

app.whenReady().then(() => {
  const smokeArg = process.argv.find((arg) => arg.startsWith('--pty-smoke'));
  const uiSmokeArg = process.argv.find((arg) => arg.startsWith('--ui-smoke'));
  if (smokeArg) {
    const report = smokeArg.includes('=') ? smokeArg.slice(smokeArg.indexOf('=') + 1) : null;
    runPtySmoke(report);
  } else if (uiSmokeArg) {
    const screenshot = uiSmokeArg.includes('=') ? uiSmokeArg.slice(uiSmokeArg.indexOf('=') + 1) : null;
    runUiSmoke(screenshot);
  } else {
    createWindow();
  }
});
