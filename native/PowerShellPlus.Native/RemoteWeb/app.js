/* global Terminal */
(() => {
  'use strict';

  const runtime = new Map();
  const pendingRequests = new Map();
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
  let fontOffset = 0;
  let quickCommands = [];
  let reconnectTimer;
  let syncTimer;
  let intentionallyClosed = false;

  const terminalTheme = {
    background: '#1e1e2e', foreground: '#cdd6f4', cursor: '#cdd6f4', cursorAccent: '#1e1e2e',
    selectionBackground: '#585b7080', black: '#45475a', red: '#f38ba8', green: '#a6e3a1', yellow: '#f9e2af',
    blue: '#89b4fa', magenta: '#cba6f7', cyan: '#94e2d5', white: '#bac2de', brightBlack: '#585b70',
    brightRed: '#f38ba8', brightGreen: '#a6e3a1', brightYellow: '#f9e2af', brightBlue: '#89b4fa',
    brightMagenta: '#cba6f7', brightCyan: '#94e2d5', brightWhite: '#cdd6f4'
  };

  function setConnection(text, state) {
    connectionState.lastChild.textContent = text;
    connectionState.className = `status ${state || ''}`;
  }

  function setAccessMode(enabled) {
    allowInput = Boolean(enabled);
    accessMode.textContent = allowInput ? 'Remote typing enabled' : 'Read-only — enable typing on the computer';
    footer.classList.toggle('control-enabled', allowInput);
    runtime.forEach(value => {
      value.terminal.options.disableStdin = !allowInput;
      value.commandInput.disabled = !allowInput;
      value.sendButton.disabled = !allowInput;
    });
  }

  function activateSession(id, focus = false) {
    if (!runtime.has(id)) return;
    activeSessionId = id;
    runtime.forEach((value, key) => value.card.classList.toggle('active', key === id));
    tabs.querySelectorAll('button').forEach(button => button.classList.toggle('active', button.dataset.id === id));
    if (focusMode) runtime.get(id).card.scrollIntoView({ block: 'nearest' });
    if (focus) runtime.get(id).terminal.focus();
    scheduleAllFits();
  }

  function sendJson(message) {
    if (!socket || socket.readyState !== WebSocket.OPEN || socket.bufferedAmount > 65536) return false;
    socket.send(JSON.stringify(message));
    return true;
  }

  function queueInput(id, data) {
    if (!allowInput) return;
    sendJson({ type: 'input', sessionId: id, data });
  }

  function safeGrid(columns, rows) {
    return {
      columns: Math.max(2, Math.min(500, Number(columns) || 120)),
      rows: Math.max(2, Math.min(300, Number(rows) || 32))
    };
  }

  function fontStack(fontFace) {
    const safe = String(fontFace || 'Cascadia Mono').replace(/["\\]/g, '');
    return `"${safe}", "Cascadia Mono", "Cascadia Code", Consolas, monospace`;
  }

  function applyGrid(value, columns, rows) {
    const next = safeGrid(columns, rows);
    if (value.columns !== next.columns || value.rows !== next.rows) {
      value.columns = next.columns;
      value.rows = next.rows;
      value.terminal.resize(next.columns, next.rows);
    }
    scheduleFit(value);
  }

  function scheduleFit(value) {
    cancelAnimationFrame(value.fitFrame);
    value.fitFrame = requestAnimationFrame(() => fitTerminal(value));
  }

  function fitTerminal(value) {
    if (!value.card.isConnected || value.card.offsetParent === null || value.host.clientWidth < 20 || value.host.clientHeight < 20) return;
    const preferred = Math.max(8, Math.min(24, value.nativeFontSize));
    value.terminal.options.fontFamily = fontStack(value.fontFace);
    value.terminal.options.fontSize = preferred;
    value.terminal.resize(value.columns, value.rows);
    value.fitFrame = requestAnimationFrame(() => {
      const screen = value.terminal.element?.querySelector('.xterm-screen');
      if (!screen) return;
      const hostStyle = getComputedStyle(value.host);
      const availableWidth = value.host.clientWidth - parseFloat(hostStyle.paddingLeft) - parseFloat(hostStyle.paddingRight);
      const availableHeight = value.host.clientHeight - parseFloat(hostStyle.paddingTop) - parseFloat(hostStyle.paddingBottom);
      const bounds = screen.getBoundingClientRect();
      if (bounds.width <= 0 || bounds.height <= 0 || availableWidth <= 0 || availableHeight <= 0) return;
      const scale = Math.min(1, availableWidth / bounds.width, availableHeight / bounds.height);
      const zoom = Math.pow(1.12, fontOffset);
      const fitted = Math.max(5, Math.min(24, Math.floor(preferred * scale * zoom * 10) / 10));
      if (Math.abs(value.terminal.options.fontSize - fitted) > 0.05) {
        value.terminal.options.fontSize = fitted;
        value.terminal.resize(value.columns, value.rows);
      }
    });
  }

  function scheduleAllFits() {
    runtime.forEach(scheduleFit);
  }

  function growCommandInput(input) {
    input.style.height = 'auto';
    input.style.height = `${Math.min(96, Math.max(30, input.scrollHeight))}px`;
  }

  function setCommandStatus(value, text, failed = false) {
    value.commandStatus.textContent = text;
    value.commandStatus.classList.toggle('failed', failed);
    clearTimeout(value.statusTimer);
    if (text) value.statusTimer = setTimeout(() => { value.commandStatus.textContent = ''; }, 2200);
  }

  function requestCommand(value, type) {
    if (!allowInput) return;
    const command = value.commandInput.value.trim();
    if (!command) return;
    const requestId = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 9)}`;
    const message = { type, requestId, sessionId: value.id, command };
    if (type === 'command' && Number.isInteger(value.selectedQueueIndex)) message.queueIndex = value.selectedQueueIndex;
    if (!sendJson(message)) {
      setCommandStatus(value, 'Not connected', true);
      return;
    }
    pendingRequests.set(requestId, { value, command, type });
    value.commandInput.value = '';
    value.selectedQueueIndex = null;
    growCommandInput(value.commandInput);
    closePopover(value);
    setCommandStatus(value, type === 'queue-add' ? 'Adding to queue…' : 'Sending…');
    scheduleFit(value);
  }

  function closePopover(value) {
    value.popover.hidden = true;
    value.popoverMode = null;
  }

  function closeOtherPopovers(except) {
    runtime.forEach(value => { if (value !== except) closePopover(value); });
  }

  function appendPopoverItem(value, primary, secondary, command, queueIndex) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'popover-item';
    const label = document.createElement('strong');
    const detail = document.createElement('span');
    label.textContent = primary;
    detail.textContent = secondary;
    button.append(label, detail);
    button.addEventListener('click', () => {
      value.commandInput.value = command;
      value.selectedQueueIndex = Number.isInteger(queueIndex) ? queueIndex : null;
      growCommandInput(value.commandInput);
      closePopover(value);
      value.commandInput.focus();
      scheduleFit(value);
    });
    value.popoverList.append(button);
  }

  function renderPopover(value, mode) {
    const sameMode = value.popoverMode === mode && !value.popover.hidden;
    closeOtherPopovers(value);
    if (sameMode) { closePopover(value); return; }
    value.popoverMode = mode;
    value.popoverTitle.textContent = mode === 'queue' ? 'Command queue' : 'Quick commands';
    value.popoverList.replaceChildren();
    if (mode === 'queue') {
      const current = value.commandInput.value.trim();
      if (current) {
        const add = document.createElement('button');
        add.type = 'button';
        add.className = 'popover-add';
        add.textContent = '＋ Add current input to queue';
        add.disabled = !allowInput;
        add.addEventListener('click', () => requestCommand(value, 'queue-add'));
        value.popoverList.append(add);
      }
      if (!value.pendingCommands.length) {
        const empty = document.createElement('p');
        empty.className = 'popover-empty';
        empty.textContent = 'No queued commands. Ctrl/Cmd+Enter adds the current input.';
        value.popoverList.append(empty);
      } else {
        value.pendingCommands.forEach((command, index) =>
          appendPopoverItem(value, `${index + 1}`, command, command, index));
      }
    } else if (!quickCommands.length) {
      const empty = document.createElement('p');
      empty.className = 'popover-empty';
      empty.textContent = 'No commands are marked for Quick Access.';
      value.popoverList.append(empty);
    } else {
      quickCommands.forEach(snippet => appendPopoverItem(value, snippet.name, snippet.category, snippet.command, null));
    }
    value.popover.hidden = false;
  }

  function updateQueueDisplay(value) {
    const count = value.pendingCommands.length;
    value.queueBadge.textContent = count > 99 ? '99+' : String(count);
    value.queueBadge.hidden = count === 0;
    value.queueButton.setAttribute('aria-label', count ? `View ${count} queued commands` : 'View command queue');
    if (value.popoverMode === 'queue' && !value.popover.hidden) {
      value.popoverMode = null;
      renderPopover(value, 'queue');
    }
  }

  function browseQueue(value, direction) {
    if (!value.pendingCommands.length) return false;
    if (!Number.isInteger(value.selectedQueueIndex)) value.selectedQueueIndex = direction < 0 ? value.pendingCommands.length - 1 : 0;
    else value.selectedQueueIndex = Math.max(0, Math.min(value.pendingCommands.length - 1, value.selectedQueueIndex + direction));
    value.commandInput.value = value.pendingCommands[value.selectedQueueIndex];
    value.commandInput.select();
    growCommandInput(value.commandInput);
    return true;
  }

  function createCommandArea(value) {
    const area = document.createElement('div');
    area.className = 'command-area';
    const row = document.createElement('div');
    row.className = 'command-row';
    const quickButton = document.createElement('button');
    quickButton.type = 'button';
    quickButton.className = 'command-icon quick-button';
    quickButton.textContent = 'ϟ';
    quickButton.setAttribute('aria-label', 'Quick commands');
    const input = document.createElement('textarea');
    input.className = 'command-input';
    input.rows = 1;
    input.spellcheck = false;
    input.placeholder = 'Run a command in this session';
    input.setAttribute('aria-label', 'Session command line');
    input.disabled = !allowInput;
    const queueButton = document.createElement('button');
    queueButton.type = 'button';
    queueButton.className = 'queue-button';
    queueButton.append(document.createTextNode('Queue'));
    const badge = document.createElement('span');
    badge.className = 'queue-badge';
    badge.hidden = true;
    queueButton.append(badge);
    const sendButton = document.createElement('button');
    sendButton.type = 'button';
    sendButton.className = 'command-icon send-button';
    sendButton.textContent = '▶';
    sendButton.setAttribute('aria-label', 'Run command');
    sendButton.disabled = !allowInput;
    const status = document.createElement('span');
    status.className = 'command-status';
    status.setAttribute('aria-live', 'polite');

    const popover = document.createElement('div');
    popover.className = 'command-popover';
    popover.hidden = true;
    const popoverHeader = document.createElement('div');
    popoverHeader.className = 'popover-header';
    const popoverTitle = document.createElement('strong');
    const popoverClose = document.createElement('button');
    popoverClose.type = 'button';
    popoverClose.textContent = '×';
    popoverClose.setAttribute('aria-label', 'Close menu');
    const popoverList = document.createElement('div');
    popoverList.className = 'popover-list';
    popoverHeader.append(popoverTitle, popoverClose);
    popover.append(popoverHeader, popoverList);
    row.append(quickButton, input, queueButton, sendButton);
    area.append(row, status, popover);

    Object.assign(value, { commandArea: area, commandInput: input, queueButton, queueBadge: badge, sendButton, commandStatus: status, popover, popoverTitle, popoverList });
    quickButton.addEventListener('click', () => renderPopover(value, 'quick'));
    queueButton.addEventListener('click', () => renderPopover(value, 'queue'));
    sendButton.addEventListener('click', () => requestCommand(value, 'command'));
    popoverClose.addEventListener('click', () => closePopover(value));
    input.addEventListener('input', () => { value.selectedQueueIndex = null; growCommandInput(input); scheduleFit(value); });
    input.addEventListener('focus', () => activateSession(value.id));
    input.addEventListener('keydown', event => {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        requestCommand(value, 'queue-add');
      } else if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        requestCommand(value, 'command');
      } else if (event.key === 'ArrowUp' && input.selectionStart === 0 && browseQueue(value, -1)) event.preventDefault();
      else if (event.key === 'ArrowDown' && input.selectionStart === input.value.length && browseQueue(value, 1)) event.preventDefault();
      else if (event.key === 'Escape') closePopover(value);
    });
    growCommandInput(input);
    return area;
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

    const dimensions = safeGrid(session.columns, session.rows);
    const terminal = new Terminal({
      cols: dimensions.columns,
      rows: dimensions.rows,
      cursorStyle: 'bar',
      cursorBlink: true,
      disableStdin: !allowInput,
      fontFamily: fontStack(session.fontFace),
      fontSize: Math.max(8, Number(session.fontSize) || 12),
      lineHeight: 1.12,
      letterSpacing: 0,
      scrollback: 8000,
      allowTransparency: true,
      theme: terminalTheme
    });
    terminal.open(host);

    const tab = document.createElement('button');
    tab.type = 'button';
    tab.dataset.id = session.id;
    tab.textContent = session.name;
    tab.addEventListener('click', () => activateSession(session.id, allowInput));
    tabs.append(tab);

    const value = {
      id: session.id, card, terminal, host, title, directory, tab,
      columns: dimensions.columns, rows: dimensions.rows,
      fontFace: session.fontFace || 'Cascadia Mono', nativeFontSize: Number(session.fontSize) || 12,
      pendingCommands: session.pendingCommands || [], selectedQueueIndex: null, popoverMode: null, fitFrame: 0
    };
    card.append(createCommandArea(value));
    runtime.set(session.id, value);
    terminal.onData(data => queueInput(session.id, data));
    card.addEventListener('pointerdown', () => activateSession(session.id));
    value.resizeObserver = new ResizeObserver(() => scheduleFit(value));
    value.resizeObserver.observe(host);
    updateQueueDisplay(value);
    scheduleFit(value);
  }

  function updateSessions(sessions) {
    const wanted = new Set(sessions.map(value => value.id));
    for (const [id, value] of runtime) {
      if (wanted.has(id)) continue;
      value.resizeObserver.disconnect();
      cancelAnimationFrame(value.fitFrame);
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
      value.pendingCommands = Array.isArray(session.pendingCommands) ? session.pendingCommands : [];
      value.fontFace = session.fontFace || value.fontFace;
      value.nativeFontSize = Number(session.fontSize) || value.nativeFontSize;
      applyGrid(value, session.columns, session.rows);
      updateQueueDisplay(value);
    }
    emptyState.hidden = sessions.length !== 0;
    grid.classList.toggle('focus-mode', focusMode);
    if (!runtime.has(activeSessionId)) activeSessionId = sessions[0]?.id;
    if (activeSessionId) activateSession(activeSessionId);
  }

  function settleRequest(message) {
    const pending = pendingRequests.get(message.requestId);
    if (!pending) return;
    pendingRequests.delete(message.requestId);
    if (message.accepted) setCommandStatus(pending.value, pending.type === 'queue-add' ? 'Queued' : 'Sent');
    else {
      if (!pending.value.commandInput.value) {
        pending.value.commandInput.value = pending.command;
        growCommandInput(pending.value.commandInput);
      }
      setCommandStatus(pending.value, 'Command was rejected', true);
    }
  }

  function handleMessage(message) {
    if (message.type === 'sessions') {
      quickCommands = Array.isArray(message.quickCommands) ? message.quickCommands : [];
      setAccessMode(message.allowInput);
      updateSessions(message.sessions || []);
    } else if (message.type === 'snapshot') {
      const value = runtime.get(message.sessionId);
      if (value) {
        applyGrid(value, message.columns, message.rows);
        value.terminal.reset();
        value.terminal.resize(value.columns, value.rows);
        value.terminal.write(message.data || '', () => { value.terminal.scrollToBottom(); scheduleFit(value); });
      }
    } else if (message.type === 'output') {
      const value = runtime.get(message.sessionId);
      if (value) {
        applyGrid(value, message.columns, message.rows);
        value.terminal.write(message.data || '');
      }
    } else if (message.type === 'resync' && socket?.readyState === WebSocket.OPEN) {
      sendJson({ type: 'sync', snapshots: true });
    } else if (message.type === 'input-denied') {
      setAccessMode(false);
    } else if (message.type === 'command-ack' || message.type === 'queue-ack') {
      settleRequest(message);
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
        if (socket.readyState === WebSocket.OPEN) sendJson({ type: 'sync' });
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
    setTimeout(scheduleAllFits, 80);
  });
  document.getElementById('fontDown').addEventListener('click', () => { fontOffset = Math.max(-6, fontOffset - 1); scheduleAllFits(); });
  document.getElementById('fontUp').addEventListener('click', () => { fontOffset = Math.min(10, fontOffset + 1); scheduleAllFits(); });
  document.getElementById('keyboardButton').addEventListener('click', () => {
    if (activeSessionId) activateSession(activeSessionId, true);
  });
  document.addEventListener('pointerdown', event => {
    if (!event.target.closest('.command-area')) runtime.forEach(closePopover);
  });
  const refitViewport = () => {
    document.documentElement.style.setProperty('--visual-height', `${window.visualViewport?.height || window.innerHeight}px`);
    scheduleAllFits();
    setTimeout(scheduleAllFits, 120);
  };
  window.addEventListener('resize', refitViewport);
  window.addEventListener('orientationchange', refitViewport);
  window.visualViewport?.addEventListener('resize', refitViewport);
  screen.orientation?.addEventListener('change', refitViewport);
  window.addEventListener('beforeunload', () => { intentionallyClosed = true; socket?.close(1000, 'Page closed'); });

  document.getElementById('layoutButton').textContent = focusMode ? 'All' : 'Focus';
  refitViewport();
  hasSession().then(authenticated => {
    pairingOverlay.hidden = authenticated;
    if (authenticated) connectSocket(); else setConnection('Pairing required', 'offline');
  });
})();
