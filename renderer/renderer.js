/* global Terminal, FitAddon */
const api = window.powerShellPlus;
const runtime = new Map();
let workspace;
let shellOptions = [];
let saveTimer;
let automationTimer;
let activeSection = 'sessions';

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const id = () => crypto.randomUUID();

function element(tag, className, text) {
  const value = document.createElement(tag);
  if (className) value.className = className;
  if (text !== undefined) value.textContent = text;
  return value;
}

function setStatus(message, tone = 'normal') {
  $('#statusMessage').textContent = message;
  const dot = $('.status-dot');
  dot.style.background = tone === 'error' ? '#f87171' : tone === 'busy' ? '#fbbf24' : '#34d399';
  dot.style.boxShadow = `0 0 7px ${dot.style.background}`;
}

function selectedProfile() {
  return workspace.sessions.find((session) => session.id === workspace.activeSessionId) || workspace.sessions[0] || null;
}

function defaultProfile() {
  return {
    id: id(),
    name: `PowerShell ${workspace.sessions.length + 1}`,
    shell: shellOptions[0]?.path || 'powershell.exe',
    cwd: selectedProfile()?.cwd || workspace.sessions[0]?.cwd || '',
    startupCommand: '',
    accent: ['#a1a1aa', '#71717a', '#d4d4d8', '#52525b', '#e4e4e7'][workspace.sessions.length % 5],
    autoStart: true
  };
}

async function boot() {
  bindStaticEvents();
  api.onSessionData(({ id: sessionId, data }) => {
    const session = runtime.get(sessionId);
    if (!session) return;
    session.terminal.write(data);
    session.output = `${session.output}${stripAnsi(data)}`.slice(-1_000_000);
  });
  api.onSessionExit(({ id: sessionId, exitCode }) => {
    const session = runtime.get(sessionId);
    if (!session) return;
    updateTerminalState(sessionId, `Exited (${exitCode})`, 'exited');
    session.running = false;
    renderSessionList();
    updateCounts();
  });

  [workspace, shellOptions] = await Promise.all([api.loadWorkspace(), api.shellCandidates()]);
  normalizeWorkspace();
  applyWorkspaceToUi();
  for (const profile of workspace.sessions) createTerminalCard(profile, profile.autoStart !== false);
  if (!workspace.activeSessionId && workspace.sessions[0]) workspace.activeSessionId = workspace.sessions[0].id;
  selectSession(workspace.activeSessionId, false);
  renderAllLists();
  applyLayout();
  startAutomationClock();
  setStatus('ConPTY workspace ready');
}

function normalizeWorkspace() {
  workspace.sessions ||= [];
  workspace.snippets ||= [];
  workspace.automations ||= [];
  workspace.settings ||= {};
  workspace.settings.fontSize = Number(workspace.settings.fontSize) || 14;
  workspace.settings.fontFamily ||= 'Cascadia Mono, Consolas, monospace';
  workspace.layout ||= 'grid';
  if (!workspace.sessions.some((session) => session.id === workspace.activeSessionId)) workspace.activeSessionId = workspace.sessions[0]?.id || null;
}

function applyWorkspaceToUi() {
  $('#workspaceTitle').textContent = workspace.name || 'Main workspace';
  $('#fontSize').value = workspace.settings.fontSize;
  $('#fontSizeValue').textContent = `${workspace.settings.fontSize}px`;
  $('#fontFamily').value = workspace.settings.fontFamily;
  $$('.layout-switcher button').forEach((button) => button.classList.toggle('active', button.dataset.layout === workspace.layout));
}

function createTerminalCard(profile, start = true) {
  if (runtime.has(profile.id)) return runtime.get(profile.id);
  const card = $('#terminalCardTemplate').content.firstElementChild.cloneNode(true);
  card.dataset.sessionId = profile.id;
  card.style.setProperty('--terminal-accent', profile.accent || '#a1a1aa');
  $('.terminal-name', card).textContent = profile.name;
  $('.terminal-accent', card).style.background = profile.accent || '#a1a1aa';
  $('.terminal-accent', card).style.boxShadow = `0 0 8px ${profile.accent || '#a1a1aa'}`;
  card.addEventListener('pointerdown', () => selectSession(profile.id));
  $('.terminal-actions', card).addEventListener('click', (event) => {
    event.stopPropagation();
    const action = event.target.closest('button')?.dataset.terminalAction;
    if (action) handleTerminalAction(profile.id, action);
  });

  $('#terminalWorkspace').append(card);
  const terminal = new Terminal({
    cursorBlink: true,
    cursorStyle: 'bar',
    cursorWidth: 2,
    allowProposedApi: false,
    convertEol: false,
    fontFamily: workspace.settings.fontFamily,
    fontSize: workspace.settings.fontSize,
    fontWeight: '400',
    fontWeightBold: '650',
    lineHeight: 1.18,
    letterSpacing: 0,
    scrollback: 10000,
    smoothScrollDuration: 100,
    theme: {
      background: '#0b0b0e',
      foreground: '#d8d8dc',
      cursor: profile.accent || '#d4d4d8',
      cursorAccent: '#0b0b0e',
      selectionBackground: '#52525b88',
      black: '#18181b', red: '#f87171', green: '#4ade80', yellow: '#facc15', blue: '#60a5fa', magenta: '#c084fc', cyan: '#22d3ee', white: '#e4e4e7',
      brightBlack: '#71717a', brightRed: '#fca5a5', brightGreen: '#86efac', brightYellow: '#fde047', brightBlue: '#93c5fd', brightMagenta: '#d8b4fe', brightCyan: '#67e8f9', brightWhite: '#fafafa'
    }
  });
  const fit = new FitAddon.FitAddon();
  terminal.loadAddon(fit);
  terminal.open($('.terminal-host', card));
  terminal.onData((data) => api.writeSession(profile.id, data));
  terminal.onResize(({ cols, rows }) => api.resizeSession(profile.id, cols, rows));
  terminal.attachCustomKeyEventHandler((event) => {
    if (event.type !== 'keydown') return true;
    if (event.ctrlKey && event.shiftKey && event.code === 'KeyC' && terminal.hasSelection()) {
      navigator.clipboard?.writeText(terminal.getSelection());
      return false;
    }
    return true;
  });

  let resizeFrame;
  const observer = new ResizeObserver(() => {
    cancelAnimationFrame(resizeFrame);
    resizeFrame = requestAnimationFrame(() => fitTerminal(profile.id));
  });
  observer.observe(card);
  const entry = { profile, card, terminal, fit, observer, running: false, output: '' };
  runtime.set(profile.id, entry);
  if (start) startSession(profile.id);
  else {
    terminal.writeln('\x1b[38;5;245mSession is stopped. Use Restart to open its ConPTY.\x1b[0m');
    updateTerminalState(profile.id, 'Stopped', 'exited');
  }
  requestAnimationFrame(() => fitTerminal(profile.id));
  return entry;
}

async function startSession(sessionId) {
  const entry = runtime.get(sessionId);
  if (!entry) return;
  updateTerminalState(sessionId, 'Starting…');
  try {
    const result = await api.createSession(entry.profile);
    entry.running = true;
    updateTerminalState(sessionId, `ConPTY · PID ${result.pid}`, 'running');
    fitTerminal(sessionId);
    renderSessionList();
    updateCounts();
  } catch (error) {
    entry.terminal.writeln(`\r\n\x1b[31mUnable to start PowerShell: ${error.message}\x1b[0m`);
    updateTerminalState(sessionId, 'Start failed', 'exited');
    setStatus(`Could not start ${entry.profile.name}`, 'error');
  }
}

async function restartSession(sessionId) {
  const entry = runtime.get(sessionId);
  if (!entry) return;
  updateTerminalState(sessionId, 'Restarting…');
  try {
    const result = await api.restartSession(sessionId, entry.profile);
    entry.running = true;
    updateTerminalState(sessionId, `ConPTY · PID ${result.pid}`, 'running');
    fitTerminal(sessionId);
    setStatus(`${entry.profile.name} restarted`);
  } catch (error) {
    updateTerminalState(sessionId, 'Restart failed', 'exited');
    setStatus(error.message, 'error');
  }
}

async function stopSession(sessionId) {
  const entry = runtime.get(sessionId);
  if (!entry) return;
  await api.killSession(sessionId);
  entry.running = false;
  updateTerminalState(sessionId, 'Stopped', 'exited');
  entry.terminal.writeln('\r\n\x1b[38;5;245m[Session stopped]\x1b[0m');
  renderSessionList(); updateCounts(); setStatus(`${entry.profile.name} stopped`);
}

async function removeSession(sessionId, ask = true) {
  const profile = workspace.sessions.find((session) => session.id === sessionId);
  if (!profile) return;
  if (ask && !confirm(`Close “${profile.name}” and remove it from this workspace?`)) return;
  await api.killSession(sessionId);
  const entry = runtime.get(sessionId);
  entry?.observer.disconnect();
  entry?.terminal.dispose();
  entry?.card.remove();
  runtime.delete(sessionId);
  workspace.sessions = workspace.sessions.filter((session) => session.id !== sessionId);
  if (workspace.activeSessionId === sessionId) workspace.activeSessionId = workspace.sessions[0]?.id || null;
  renderSessionList();
  selectSession(workspace.activeSessionId, false);
  applyLayout();
  scheduleSave();
  updateCounts();
}

function updateTerminalState(sessionId, text, className = '') {
  const state = runtime.get(sessionId)?.card.querySelector('.terminal-state');
  if (!state) return;
  state.textContent = text;
  state.className = `terminal-state ${className}`.trim();
}

function handleTerminalAction(sessionId, action) {
  const entry = runtime.get(sessionId);
  if (!entry) return;
  if (action === 'clear') entry.terminal.clear();
  if (action === 'export') api.exportOutput(entry.profile.name, entry.output);
  if (action === 'stop') stopSession(sessionId);
  if (action === 'restart') restartSession(sessionId);
  if (action === 'edit') openSessionModal(entry.profile);
  if (action === 'close') removeSession(sessionId);
}

function selectSession(sessionId, focus = true) {
  if (!sessionId || !runtime.has(sessionId)) return;
  workspace.activeSessionId = sessionId;
  for (const [idValue, entry] of runtime) entry.card.classList.toggle('active', idValue === sessionId);
  $$('.session-item').forEach((item) => item.classList.toggle('active', item.dataset.id === sessionId));
  if (workspace.layout === 'focus') applyLayout();
  if (focus) runtime.get(sessionId).terminal.focus();
  scheduleSave();
}

function fitTerminal(sessionId) {
  const entry = runtime.get(sessionId);
  if (!entry || !entry.card.isConnected || entry.card.offsetParent === null) return;
  try { entry.fit.fit(); } catch {}
}

function fitAll() {
  for (const sessionId of runtime.keys()) fitTerminal(sessionId);
}

function applyLayout() {
  const host = $('#terminalWorkspace');
  const count = Math.max(1, workspace.sessions.length);
  const columns = Math.ceil(Math.sqrt(count));
  const rows = Math.ceil(count / columns);
  host.className = `terminal-workspace layout-${workspace.layout}`;
  host.style.setProperty('--session-count', count);
  host.style.setProperty('--layout-columns', columns);
  host.style.setProperty('--layout-rows', rows);
  $$('.layout-switcher button').forEach((button) => button.classList.toggle('active', button.dataset.layout === workspace.layout));
  setTimeout(fitAll, 80);
  scheduleSave();
}

function renderAllLists() {
  renderSessionList();
  renderSnippetList();
  renderAutomationList();
  updateCounts();
}

function renderSessionList() {
  const host = $('#sessionList');
  const query = $('#sessionSearch').value.trim().toLowerCase();
  host.replaceChildren();
  const profiles = workspace.sessions.filter((session) => `${session.name} ${session.cwd}`.toLowerCase().includes(query));
  if (!profiles.length) { host.append(element('div', 'empty-list', workspace.sessions.length ? 'No matching sessions.' : 'No sessions yet. Create one to open a real PowerShell terminal.')); return; }
  for (const profile of profiles) {
    const item = element('button', `list-item session-item${profile.id === workspace.activeSessionId ? ' active' : ''}`);
    item.dataset.id = profile.id;
    const accent = element('span', 'item-accent'); accent.style.background = profile.accent; accent.style.color = profile.accent;
    const copy = element('span', 'item-copy'); copy.append(element('strong', '', profile.name));
    const running = runtime.get(profile.id)?.running;
    copy.append(element('span', '', running ? `Running · ${shortPath(profile.cwd)}` : `Stopped · ${shortPath(profile.cwd)}`));
    const tools = element('span', 'item-tools');
    const restart = element('button', '', '↻'); restart.title = 'Restart'; restart.addEventListener('click', (event) => { event.stopPropagation(); restartSession(profile.id); });
    const edit = element('button', '', '•••'); edit.title = 'Edit'; edit.addEventListener('click', (event) => { event.stopPropagation(); openSessionModal(profile); });
    tools.append(restart, edit);
    item.append(accent, copy, tools);
    item.addEventListener('click', () => selectSession(profile.id));
    host.append(item);
  }
}

function renderSnippetList() {
  const host = $('#snippetList');
  const query = $('#snippetSearch').value.trim().toLowerCase();
  host.replaceChildren();
  const snippets = workspace.snippets.filter((snippet) => `${snippet.name} ${snippet.category} ${snippet.command}`.toLowerCase().includes(query));
  if (!snippets.length) { host.append(element('div', 'empty-list', workspace.snippets.length ? 'No matching commands.' : 'Your command library is empty.')); return; }
  for (const snippet of snippets) {
    const item = element('button', 'list-item');
    const accent = element('span', 'item-accent'); accent.style.background = '#71717a'; accent.style.color = '#71717a';
    const copy = element('span', 'item-copy'); copy.append(element('strong', '', snippet.name)); copy.append(element('span', '', `${snippet.category || 'General'} · ${snippet.command}`));
    const tools = element('span', 'item-tools');
    const all = element('button', '', '⇉'); all.title = 'Run in all sessions'; all.addEventListener('click', (event) => { event.stopPropagation(); runCommand(snippet.command, true); });
    const edit = element('button', '', '•••'); edit.title = 'Edit'; edit.addEventListener('click', (event) => { event.stopPropagation(); openSnippetModal(snippet); });
    const remove = element('button', '', '×'); remove.title = 'Delete'; remove.addEventListener('click', (event) => { event.stopPropagation(); if (confirm(`Delete “${snippet.name}”?`)) { workspace.snippets = workspace.snippets.filter((value) => value.id !== snippet.id); renderSnippetList(); scheduleSave(); } });
    tools.append(all, edit, remove); item.append(accent, copy, tools);
    item.addEventListener('click', () => runCommand(snippet.command, false));
    host.append(item);
  }
}

function renderAutomationList() {
  const host = $('#automationList');
  host.replaceChildren();
  if (!workspace.automations.length) { host.append(element('div', 'empty-list', 'No automations yet. Schedule a command for one terminal or every terminal.')); return; }
  for (const automation of workspace.automations) {
    const item = element('button', 'list-item');
    const accent = element('span', 'item-accent'); accent.style.background = automation.enabled ? '#34d399' : '#52525b'; accent.style.color = accent.style.background;
    const copy = element('span', 'item-copy'); copy.append(element('strong', '', automation.name)); copy.append(element('span', '', automation.type === 'daily' ? `Daily at ${automation.dailyTime}` : `Every ${automation.intervalMinutes} min`));
    const tools = element('span', 'item-tools');
    const run = element('button', '', '▶'); run.title = 'Run now'; run.addEventListener('click', (event) => { event.stopPropagation(); executeAutomation(automation); });
    const toggle = element('button', '', automation.enabled ? 'Ⅱ' : '▶'); toggle.title = automation.enabled ? 'Disable' : 'Enable'; toggle.addEventListener('click', (event) => { event.stopPropagation(); automation.enabled = !automation.enabled; renderAutomationList(); scheduleSave(); });
    const edit = element('button', '', '•••'); edit.title = 'Edit'; edit.addEventListener('click', (event) => { event.stopPropagation(); openAutomationModal(automation); });
    const remove = element('button', '', '×'); remove.title = 'Delete'; remove.addEventListener('click', (event) => { event.stopPropagation(); if (confirm(`Delete “${automation.name}”?`)) { workspace.automations = workspace.automations.filter((value) => value.id !== automation.id); renderAutomationList(); scheduleSave(); } });
    tools.append(run, toggle, edit, remove); item.append(accent, copy, tools);
    item.addEventListener('click', () => openAutomationModal(automation));
    host.append(item);
  }
}

function updateCounts() {
  const running = [...runtime.values()].filter((entry) => entry.running).length;
  const total = workspace?.sessions.length || 0;
  $('#sessionCount').textContent = `${running} running · ${total} total`;
}

function runCommand(command, all = false) {
  if (!command?.trim()) return;
  const targets = all ? [...runtime.values()] : [runtime.get(workspace.activeSessionId)].filter(Boolean);
  const runningTargets = targets.filter((entry) => entry.running);
  for (const entry of runningTargets) api.writeSession(entry.profile.id, `${command}\r`);
  if (!runningTargets.length) { setStatus('No running target session', 'error'); return; }
  setStatus(`Command sent to ${runningTargets.length} session${runningTargets.length === 1 ? '' : 's'}`);
  if (!all) runtime.get(workspace.activeSessionId)?.terminal.focus();
}

function openSessionModal(profile = null) {
  const value = profile || defaultProfile();
  $('#sessionModalTitle').textContent = profile ? 'Edit session' : 'New session';
  $('#sessionId').value = profile?.id || '';
  $('#sessionName').value = value.name;
  $('#sessionAccent').value = value.accent || '#a1a1aa';
  $('#sessionCwd').value = value.cwd || '';
  $('#sessionStartup').value = value.startupCommand || '';
  $('#sessionAutoStart').checked = value.autoStart !== false;
  const select = $('#sessionShell'); select.replaceChildren();
  for (const candidate of shellOptions) { const option = element('option', '', candidate.name); option.value = candidate.path; select.append(option); }
  if (value.shell && !shellOptions.some((candidate) => candidate.path === value.shell)) { const option = element('option', '', value.shell); option.value = value.shell; select.append(option); }
  select.value = value.shell || shellOptions[0]?.path || 'powershell.exe';
  showModal('sessionModal');
  $('#sessionName').focus(); $('#sessionName').select();
}

async function saveSessionForm(event) {
  event.preventDefault();
  const existingId = $('#sessionId').value;
  const existing = workspace.sessions.find((session) => session.id === existingId);
  const profile = {
    id: existingId || id(),
    name: $('#sessionName').value.trim(),
    shell: $('#sessionShell').value,
    cwd: $('#sessionCwd').value.trim(),
    startupCommand: $('#sessionStartup').value,
    accent: $('#sessionAccent').value,
    autoStart: $('#sessionAutoStart').checked
  };
  if (!profile.name || !profile.cwd) return;
  if (existing) {
    Object.assign(existing, profile);
    const entry = runtime.get(profile.id);
    entry.profile = existing;
    $('.terminal-name', entry.card).textContent = profile.name;
    entry.card.style.setProperty('--terminal-accent', profile.accent);
    await restartSession(profile.id);
  } else {
    workspace.sessions.push(profile);
    createTerminalCard(profile, true);
  }
  workspace.activeSessionId = profile.id;
  closeModal(); renderSessionList(); selectSession(profile.id, false); applyLayout(); scheduleSave();
}

function openSnippetModal(snippet = null) {
  $('#snippetModalTitle').textContent = snippet ? 'Edit command' : 'Save command';
  $('#snippetId').value = snippet?.id || '';
  $('#snippetName').value = snippet?.name || '';
  $('#snippetCategory').value = snippet?.category || 'General';
  $('#snippetCommand').value = snippet?.command || '';
  showModal('snippetModal'); $('#snippetName').focus();
}

function saveSnippetForm(event) {
  event.preventDefault();
  const existingId = $('#snippetId').value;
  const value = { id: existingId || id(), name: $('#snippetName').value.trim(), category: $('#snippetCategory').value.trim() || 'General', command: $('#snippetCommand').value.trim() };
  const index = workspace.snippets.findIndex((snippet) => snippet.id === existingId);
  if (index >= 0) workspace.snippets[index] = value; else workspace.snippets.push(value);
  closeModal(); renderSnippetList(); scheduleSave();
}

function openAutomationModal(automation = null) {
  $('#automationModalTitle').textContent = automation ? 'Edit automation' : 'New automation';
  $('#automationId').value = automation?.id || '';
  $('#automationName').value = automation?.name || '';
  $('#automationCommand').value = automation?.command || '';
  $('#automationType').value = automation?.type || 'interval';
  $('#automationInterval').value = automation?.intervalMinutes || 60;
  $('#automationDaily').value = automation?.dailyTime || '09:00';
  $('#automationEnabled').checked = automation?.enabled !== false;
  const target = $('#automationTarget'); target.replaceChildren();
  const all = element('option', '', 'All running sessions'); all.value = '*'; target.append(all);
  for (const session of workspace.sessions) { const option = element('option', '', session.name); option.value = session.id; target.append(option); }
  target.value = automation?.targetSessionId || '*';
  toggleAutomationFields(); showModal('automationModal'); $('#automationName').focus();
}

function saveAutomationForm(event) {
  event.preventDefault();
  const existingId = $('#automationId').value;
  const existing = workspace.automations.find((automation) => automation.id === existingId);
  const value = {
    id: existingId || id(), name: $('#automationName').value.trim(), command: $('#automationCommand').value.trim(),
    targetSessionId: $('#automationTarget').value, type: $('#automationType').value,
    intervalMinutes: Math.max(1, Number($('#automationInterval').value) || 60), dailyTime: $('#automationDaily').value || '09:00',
    enabled: $('#automationEnabled').checked, lastRunAt: existing?.lastRunAt || Date.now()
  };
  const index = workspace.automations.findIndex((automation) => automation.id === existingId);
  if (index >= 0) workspace.automations[index] = value; else workspace.automations.push(value);
  closeModal(); renderAutomationList(); scheduleSave();
}

function toggleAutomationFields() {
  const daily = $('#automationType').value === 'daily';
  $('#intervalField').classList.toggle('hidden', daily);
  $('#dailyField').classList.toggle('hidden', !daily);
}

function startAutomationClock() {
  clearInterval(automationTimer);
  automationTimer = setInterval(checkAutomations, 10_000);
}

function checkAutomations() {
  const now = new Date();
  for (const automation of workspace.automations) {
    if (!automation.enabled || !automation.command) continue;
    const last = new Date(automation.lastRunAt || 0);
    let due = false;
    if (automation.type === 'daily') {
      const [hours, minutes] = (automation.dailyTime || '09:00').split(':').map(Number);
      const scheduled = new Date(now); scheduled.setHours(hours, minutes, 0, 0);
      due = now >= scheduled && last.toDateString() !== now.toDateString();
    } else {
      due = now.getTime() - last.getTime() >= Math.max(1, automation.intervalMinutes) * 60_000;
    }
    if (due) executeAutomation(automation);
  }
}

function executeAutomation(automation) {
  const entries = automation.targetSessionId === '*' ? [...runtime.values()] : [runtime.get(automation.targetSessionId)].filter(Boolean);
  const targets = entries.filter((entry) => entry.running);
  for (const entry of targets) api.writeSession(entry.profile.id, `${automation.command}\r`);
  automation.lastRunAt = Date.now();
  renderAutomationList(); scheduleSave();
  setStatus(targets.length ? `Automation “${automation.name}” ran in ${targets.length} session${targets.length === 1 ? '' : 's'}` : `Automation “${automation.name}” has no running target`, targets.length ? 'normal' : 'error');
}

function switchSection(section) {
  activeSection = section;
  $$('.side-section').forEach((panel) => panel.classList.toggle('active', panel.dataset.panel === section));
  $$('.rail-button[data-section]').forEach((button) => button.classList.toggle('active', button.dataset.section === section));
}

function showModal(modalId) {
  $('#modalBackdrop').classList.remove('hidden');
  $$('.modal').forEach((modal) => modal.classList.toggle('active', modal.id === modalId));
}

function closeModal() {
  $('#modalBackdrop').classList.add('hidden');
  $$('.modal').forEach((modal) => modal.classList.remove('active'));
}

function openPalette() {
  showModal('paletteModal');
  $('#paletteSearch').value = '';
  renderPalette('');
  $('#paletteSearch').focus();
}

function renderPalette(query) {
  const host = $('#paletteResults'); host.replaceChildren();
  const actions = [
    { icon: '＋', name: 'New PowerShell session', detail: 'Workspace action', run: () => openSessionModal() },
    { icon: '⊞', name: 'Switch to grid layout', detail: 'Layout', run: () => setLayout('grid') },
    { icon: '▣', name: 'Focus selected terminal', detail: 'Layout', run: () => setLayout('focus') },
    { icon: '⇉', name: 'Send quick command to all sessions', detail: 'Workspace action', run: () => { closeModal(); $('#quickCommand').focus(); } },
    ...workspace.sessions.map((session) => ({ icon: '›_', name: session.name, detail: `Session · ${session.cwd}`, run: () => selectSession(session.id) })),
    ...workspace.snippets.map((snippet) => ({ icon: '⌘', name: snippet.name, detail: `Command · ${snippet.category}`, run: () => runCommand(snippet.command) }))
  ].filter((action) => `${action.name} ${action.detail}`.toLowerCase().includes(query.toLowerCase()));
  for (const action of actions) {
    const item = element('button', 'palette-item');
    item.append(element('span', 'palette-item-icon', action.icon));
    const copy = element('span', 'palette-item-copy'); copy.append(element('strong', '', action.name)); copy.append(element('span', '', action.detail)); item.append(copy);
    item.addEventListener('click', () => { closeModal(); action.run(); });
    host.append(item);
  }
  if (!actions.length) host.append(element('div', 'empty-list', 'No matching action.'));
}

function setLayout(layout) {
  workspace.layout = layout;
  applyLayout();
}

function changeFont(delta) {
  workspace.settings.fontSize = Math.max(10, Math.min(24, Number(workspace.settings.fontSize) + delta));
  $('#fontSize').value = workspace.settings.fontSize;
  $('#fontSizeValue').textContent = `${workspace.settings.fontSize}px`;
  for (const entry of runtime.values()) entry.terminal.options.fontSize = workspace.settings.fontSize;
  setTimeout(fitAll, 50); scheduleSave();
}

function scheduleSave() {
  if (!workspace) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveWorkspace, 450);
}

async function saveWorkspace() {
  clearTimeout(saveTimer);
  try { await api.saveWorkspace(workspace); setStatus(`Saved ${new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}`); }
  catch (error) { setStatus(`Save failed: ${error.message}`, 'error'); }
}

function bindStaticEvents() {
  $('#minimizeWindow').addEventListener('click', api.minimize);
  $('#maximizeWindow').addEventListener('click', api.maximize);
  $('#closeWindow').addEventListener('click', api.close);
  $$('.rail-button[data-section]').forEach((button) => button.addEventListener('click', () => switchSection(button.dataset.section)));
  $('#openSettings').addEventListener('click', () => switchSection('settings'));
  $('#newSession').addEventListener('click', () => openSessionModal());
  $('#newSnippet').addEventListener('click', () => openSnippetModal());
  $('#newAutomation').addEventListener('click', () => openAutomationModal());
  $('#openPalette').addEventListener('click', openPalette);
  $('#sessionSearch').addEventListener('input', renderSessionList);
  $('#snippetSearch').addEventListener('input', renderSnippetList);
  $('#sessionForm').addEventListener('submit', saveSessionForm);
  $('#snippetForm').addEventListener('submit', saveSnippetForm);
  $('#automationForm').addEventListener('submit', saveAutomationForm);
  $('#automationType').addEventListener('change', toggleAutomationFields);
  $('#chooseCwd').addEventListener('click', async () => { const directory = await api.chooseDirectory(); if (directory) $('#sessionCwd').value = directory; });
  $$('[data-close-modal]').forEach((button) => button.addEventListener('click', closeModal));
  $('#modalBackdrop').addEventListener('pointerdown', (event) => { if (event.target === $('#modalBackdrop')) closeModal(); });
  $$('.layout-switcher button').forEach((button) => button.addEventListener('click', () => setLayout(button.dataset.layout)));
  $('#sendSelected').addEventListener('click', () => { runCommand($('#quickCommand').value); $('#quickCommand').value = ''; });
  $('#sendAll').addEventListener('click', () => { runCommand($('#quickCommand').value, true); $('#quickCommand').value = ''; });
  $('#quickCommand').addEventListener('keydown', (event) => { if (event.key === 'Enter') { runCommand(event.target.value, event.ctrlKey || event.shiftKey); event.target.value = ''; } });
  $('#increaseFont').addEventListener('click', () => changeFont(1));
  $('#decreaseFont').addEventListener('click', () => changeFont(-1));
  $('#fontSize').addEventListener('input', (event) => { workspace.settings.fontSize = Number(event.target.value); $('#fontSizeValue').textContent = `${workspace.settings.fontSize}px`; for (const entry of runtime.values()) entry.terminal.options.fontSize = workspace.settings.fontSize; setTimeout(fitAll, 40); scheduleSave(); });
  $('#fontFamily').addEventListener('change', (event) => { workspace.settings.fontFamily = event.target.value.trim() || 'Cascadia Mono, Consolas, monospace'; for (const entry of runtime.values()) entry.terminal.options.fontFamily = workspace.settings.fontFamily; setTimeout(fitAll, 40); scheduleSave(); });
  $('#openDataFolder').addEventListener('click', api.openUserData);
  $('#paletteSearch').addEventListener('input', (event) => renderPalette(event.target.value));
  $('#importWorkspace').addEventListener('click', importWorkspace);
  $('#exportWorkspace').addEventListener('click', async () => { const file = await api.exportWorkspace(workspace); if (file) setStatus('Workspace exported'); });
  window.addEventListener('resize', () => setTimeout(fitAll, 60));
  window.addEventListener('keydown', globalShortcuts, true);
}

async function importWorkspace() {
  const imported = await api.importWorkspace();
  if (!imported) return;
  for (const sessionId of [...runtime.keys()]) { await api.killSession(sessionId); const entry = runtime.get(sessionId); entry.observer.disconnect(); entry.terminal.dispose(); entry.card.remove(); }
  runtime.clear(); workspace = imported; normalizeWorkspace(); applyWorkspaceToUi();
  for (const profile of workspace.sessions) createTerminalCard(profile, profile.autoStart !== false);
  renderAllLists(); selectSession(workspace.activeSessionId, false); applyLayout(); scheduleSave();
  setStatus('Workspace imported');
}

function globalShortcuts(event) {
  if (event.key === 'Escape' && !$('#modalBackdrop').classList.contains('hidden')) { closeModal(); event.preventDefault(); return; }
  if (event.ctrlKey && event.key.toLowerCase() === 'k') { openPalette(); event.preventDefault(); return; }
  if (event.ctrlKey && event.key.toLowerCase() === 'n') { openSessionModal(); event.preventDefault(); return; }
  if (event.ctrlKey && event.key.toLowerCase() === 's') { saveWorkspace(); event.preventDefault(); return; }
  if (event.ctrlKey && event.shiftKey && event.key === 'Enter') {
    const command = $('#quickCommand').value;
    if (command) { runCommand(command, true); $('#quickCommand').value = ''; }
    event.preventDefault();
  }
}

function shortPath(value = '') {
  if (!value) return 'Default directory';
  const parts = value.replaceAll('/', '\\').split('\\').filter(Boolean);
  return parts.length > 2 ? `…\\${parts.slice(-2).join('\\')}` : value;
}

function stripAnsi(value) {
  return value.replace(/[\u001B\u009B][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:[;:]\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))/g, '').replace(/\r/g, '');
}

boot().catch((error) => {
  console.error(error);
  setStatus(`Startup failed: ${error.message}`, 'error');
});
