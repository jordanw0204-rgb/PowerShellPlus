const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('powerShellPlus', {
  platform: process.platform,
  createSession: (profile) => ipcRenderer.invoke('pty:create', profile),
  writeSession: (id, data) => ipcRenderer.send('pty:write', { id, data }),
  resizeSession: (id, cols, rows) => ipcRenderer.send('pty:resize', { id, cols, rows }),
  killSession: (id) => ipcRenderer.invoke('pty:kill', id),
  restartSession: (id, profile) => ipcRenderer.invoke('pty:restart', { id, profile }),
  onSessionData: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on('pty:data', listener);
    return () => ipcRenderer.removeListener('pty:data', listener);
  },
  onSessionExit: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on('pty:exit', listener);
    return () => ipcRenderer.removeListener('pty:exit', listener);
  },
  loadWorkspace: () => ipcRenderer.invoke('workspace:load'),
  saveWorkspace: (workspace) => ipcRenderer.invoke('workspace:save', workspace),
  importWorkspace: () => ipcRenderer.invoke('workspace:import'),
  exportWorkspace: (workspace) => ipcRenderer.invoke('workspace:export', workspace),
  exportOutput: (name, content) => ipcRenderer.invoke('output:export', { name, content }),
  chooseDirectory: () => ipcRenderer.invoke('dialog:directory'),
  shellCandidates: () => ipcRenderer.invoke('shell:candidates'),
  openUserData: () => ipcRenderer.invoke('app:open-user-data'),
  minimize: () => ipcRenderer.send('window:minimize'),
  maximize: () => ipcRenderer.send('window:maximize'),
  close: () => ipcRenderer.send('window:close')
});
