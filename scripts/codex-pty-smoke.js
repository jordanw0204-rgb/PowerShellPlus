const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const pty = require('node-pty');

const report = path.resolve(process.argv[2] || 'build/codex-pty-smoke.txt');
const shell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
let output = '';
let finished = false;

const terminal = pty.spawn(shell, ['-NoLogo', '-NoProfile'], {
  name: 'xterm-256color',
  cols: 120,
  rows: 36,
  cwd: process.cwd(),
  env: { ...process.env, TERM: 'xterm-256color', COLORTERM: 'truecolor' },
  useConpty: true
});

terminal.onData((data) => { output += data; });
terminal.onExit(() => finish(false, 'The terminal exited before Codex could initialize.'));

function finish(ok, message) {
  if (finished) return;
  finished = true;
  try { terminal.write('\x03'); } catch {}
  setTimeout(() => { try { terminal.kill(); } catch {} }, 50);
  fs.mkdirSync(path.dirname(report), { recursive: true });
  fs.writeFileSync(report, `${ok ? 'PASS' : 'FAIL'} ${message}\n\n${output}`, 'utf8');
  setTimeout(() => process.exit(ok ? 0 : 2), 100);
}

setTimeout(() => terminal.write('codex\r'), 300);
setTimeout(() => {
  const lower = output.toLowerCase();
  if (lower.includes('stdin is not a terminal')) return finish(false, 'Codex still reported redirected stdin.');
  if (lower.includes('commandnotfoundexception') || lower.includes('is not recognized as the name')) return finish(false, 'The Codex command was not available.');
  const interactiveOutput = output.length > 350 || lower.includes('openai') || lower.includes('codex');
  finish(interactiveOutput, interactiveOutput ? 'Codex launched interactively inside ConPTY without the redirected-stdin error.' : 'Codex did not produce an interactive screen before timeout.');
}, 6500);
