/* global Terminal */
(() => {
  'use strict';

  const runtime = new Map();
  const grid = document.getElementById('sessionGrid');
  const tabs = document.getElementById('sessionTabs');
  const emptyState = document.getElementById('emptyState');
  const pairingOverlay = document.getElementById('pairingOverlay');
  const connectionState = document.getElementById('connectionState');
  const accessMode = document.getElementById('accessMode');
  const footer = document.querySelector('footer');
  let socket;
  let activeSessionId;
  let allowInput = false;
  let focusMode = matchMedia('(max-width: 650px)').matches;
  let fontSize = matchMedia('(max-width: 650px)').matches ? 11 : 13;
  let reconnectTimer;
  let syncTimer;
  let intentionallyClosed = false;

  function setConnection(text, state) {
    connectionState.lastChild.textContent = text;
    connectionState.className = `status ${state || ''}`;
  }

  function setAccessMode(enabled) {
    allowInput = Boolean(enabled);
    accessMode.textContent = allowInput ? 'Remote typing enabled' : 'Read-only — enable typing on the computer';
    footer.classList.toggle('control-enabled', allowInput);
    runtime.forEach(value => { value.terminal.options.disableStdin = !allowInput; });
  }

  function activateSession(id, focus = false) {
    if (!runtime.has(id)) return;
    activeSessionId = id;
    runtime.forEach((value, key) => value.card.classList.toggle('active', key === id));
    tabs.querySelectorAll('button').forEach(button => button.classList.toggle('active', button.dataset.id === id));
    if (focusMode) runtime.get(id).card.scrollIntoView({ block: 'nearest' });
    if (focus) runtime.get(id).terminal.focus();
  }

  function queueInput(id, data) {
    if (!allowInput || !socket || socket.readyState !== WebSocket.OPEN || socket.bufferedAmount > 65536) return;
    socket.send(JSON.stringify({ type: 'input', sessionId: id, data }));
  }

  function createSession(session) {
    const card = document.createElement('section');
    card.className = 'terminal-card';
    card.dataset.id = session.id;
    const header = document.createElement('div');
    header.className = 'terminal-header';
    const dot = document.createElement('i');
    const title = document.createElement('span');
    title.className = 'terminal-title';
    title.textContent = session.name;
    const directory = document.createElement('span');
    directory.className = 'terminal-directory';
    directory.textContent = session.workingDirectory;
    const host = document.createElement('div');
    host.className = 'terminal-host';
    header.append(dot, title, directory);
    card.append(header, host);
    grid.append(card);

    const terminal = new Terminal({
      cols: 120,
      rows: 32,
      cursorStyle: 'bar',
      cursorBlink: true,
      disableStdin: !allowInput,
      fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
      fontSize,
      lineHeight: 1.12,
      letterSpacing: 0,
      scrollback: 8000,
      allowTransparency: true,
      theme: {
        background: '#1e1e2e', foreground: '#cdd6f4', cursor: '#cdd6f4', cursorAccent: '#1e1e2e',
        selectionBackground: '#585b7080', black: '#45475a', red: '#f38ba8', green: '#a6e3a1', yellow: '#f9e2af',
        blue: '#89b4fa', magenta: '#cba6f7', cyan: '#94e2d5', white: '#bac2de', brightBlack: '#585b70',
        brightRed: '#f38ba8', brightGreen: '#a6e3a1', brightYellow: '#f9e2af', brightBlue: '#89b4fa',
        brightMagenta: '#cba6f7', brightCyan: '#94e2d5', brightWhite: '#cdd6f4'
      }
    });
    terminal.open(host);
    terminal.onData(data => queueInput(session.id, data));
    card.addEventListener('pointerdown', () => activateSession(session.id));

    const tab = document.createElement('button');
    tab.type = 'button';
    tab.dataset.id = session.id;
    tab.textContent = session.name;
    tab.addEventListener('click', () => activateSession(session.id, allowInput));
    tabs.append(tab);

    runtime.set(session.id, { card, terminal, title, directory, tab });
  }

  function updateSessions(sessions) {
    const wanted = new Set(sessions.map(value => value.id));
    for (const [id, value] of runtime) {
      if (wanted.has(id)) continue;
      value.terminal.dispose();
      value.card.remove();
      value.tab.remove();
      runtime.delete(id);
    }
    for (const session of sessions) {
      if (!runtime.has(session.id)) createSession(session);
      const value = runtime.get(session.id);
      value.title.textContent = session.name;
      value.directory.textContent = session.workingDirectory;
      value.tab.textContent = session.name;
    }
    emptyState.hidden = sessions.length !== 0;
    grid.classList.toggle('focus-mode', focusMode);
    if (!runtime.has(activeSessionId)) activeSessionId = sessions[0]?.id;
    if (activeSessionId) activateSession(activeSessionId);
  }

  function handleMessage(message) {
    if (message.type === 'sessions') {
      setAccessMode(message.allowInput);
      updateSessions(message.sessions || []);
    } else if (message.type === 'snapshot') {
      const value = runtime.get(message.sessionId);
      if (value) { value.terminal.reset(); value.terminal.write(message.data || ''); }
    } else if (message.type === 'output') {
      runtime.get(message.sessionId)?.terminal.write(message.data || '');
    } else if (message.type === 'resync' && socket?.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: 'sync', snapshots: true }));
    } else if (message.type === 'input-denied') {
      setAccessMode(false);
    }
  }

  function connectSocket() {
    clearTimeout(reconnectTimer);
    clearInterval(syncTimer);
    setConnection('Connecting', '');
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(`${scheme}//${location.host}/ws`);
    socket.addEventListener('open', () => {
      setConnection('Live', 'online');
      pairingOverlay.hidden = true;
      syncTimer = setInterval(() => {
        if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'sync' }));
      }, 3000);
    });
    socket.addEventListener('message', event => {
      try { handleMessage(JSON.parse(event.data)); } catch { setConnection('Protocol error', 'offline'); }
    });
    socket.addEventListener('close', async event => {
      clearInterval(syncTimer);
      setConnection('Disconnected', 'offline');
      if (intentionallyClosed) return;
      const authenticated = event.code !== 1008 && event.code !== 4401 && await hasSession();
      if (!authenticated) {
        pairingOverlay.hidden = false;
        setConnection('Pairing required', 'offline');
        return;
      }
      reconnectTimer = setTimeout(connectSocket, 1800);
    });
    socket.addEventListener('error', () => setConnection('Connection error', 'offline'));
  }

  async function hasSession() {
    try {
      const response = await fetch('/api/sessions', { cache: 'no-store' });
      return response.ok;
    } catch { return false; }
  }

  document.getElementById('pairingForm').addEventListener('submit', async event => {
    event.preventDefault();
    const button = document.getElementById('pairButton');
    const error = document.getElementById('pairingError');
    const code = document.getElementById('pairingCode').value.trim();
    button.disabled = true;
    error.textContent = '';
    try {
      const response = await fetch('/api/pair', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
        body: JSON.stringify({ code })
      });
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || (response.status === 429 ? 'Too many attempts. Wait a minute and try again.' : 'That code did not match.'));
      }
      connectSocket();
    } catch (reason) { error.textContent = reason.message || 'Could not pair with PowerShellPlus.'; }
    finally { button.disabled = false; }
  });

  document.getElementById('layoutButton').addEventListener('click', event => {
    focusMode = !focusMode;
    grid.classList.toggle('focus-mode', focusMode);
    event.currentTarget.textContent = focusMode ? 'All' : 'Focus';
    if (activeSessionId) activateSession(activeSessionId);
  });
  document.getElementById('fontDown').addEventListener('click', () => {
    fontSize = Math.max(8, fontSize - 1);
    runtime.forEach(value => { value.terminal.options.fontSize = fontSize; });
  });
  document.getElementById('fontUp').addEventListener('click', () => {
    fontSize = Math.min(22, fontSize + 1);
    runtime.forEach(value => { value.terminal.options.fontSize = fontSize; });
  });
  document.getElementById('keyboardButton').addEventListener('click', () => {
    if (activeSessionId) activateSession(activeSessionId, true);
  });
  window.addEventListener('beforeunload', () => { intentionallyClosed = true; socket?.close(1000, 'Page closed'); });

  document.getElementById('layoutButton').textContent = focusMode ? 'All' : 'Focus';
  hasSession().then(authenticated => {
    pairingOverlay.hidden = authenticated;
    if (authenticated) connectSocket(); else setConnection('Pairing required', 'offline');
  });
})();
