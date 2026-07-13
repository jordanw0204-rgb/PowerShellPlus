using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PowerShellPlus
{
    public sealed class TerminalSessionControl : UserControl
    {
        private readonly Label titleLabel;
        private readonly Label stateLabel;
        private readonly RichTextBox output;
        private readonly TextBox input;
        private readonly Button runButton;
        private readonly Button restartButton;
        private readonly List<string> history = new List<string>();
        private int historyIndex;
        private Process process;
        private bool closing;

        public SessionProfile Profile { get; private set; }
        public bool IsRunning { get { return process != null && !SafeHasExited(process); } }
        public string CurrentInput { get { return input.Text; } set { input.Text = value ?? String.Empty; } }
        public event EventHandler SessionStateChanged;
        public event EventHandler Activated;

        public TerminalSessionControl(SessionProfile profile)
        {
            Profile = profile;
            BackColor = Color.FromArgb(30, 33, 40);
            BorderStyle = BorderStyle.FixedSingle;
            MinimumSize = new Size(250, 160);

            Panel header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(42, 46, 56) };
            titleLabel = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f), Text = profile.Name };
            stateLabel = new Label { AutoSize = false, Dock = DockStyle.Right, Width = 84, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Silver, Text = "Stopped" };
            restartButton = new Button { Dock = DockStyle.Right, Width = 34, Text = "↻", FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(42, 46, 56), TabStop = false };
            restartButton.FlatAppearance.BorderSize = 0;
            header.Controls.Add(titleLabel);
            header.Controls.Add(restartButton);
            header.Controls.Add(stateLabel);

            output = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(18, 20, 25), ForeColor = Color.FromArgb(220, 225, 232), Font = new Font("Consolas", 10.0f), WordWrap = false, DetectUrls = true, HideSelection = false };
            Panel commandBar = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(7, 5, 5, 5), BackColor = Color.FromArgb(30, 33, 40) };
            Label prompt = new Label { Dock = DockStyle.Left, Width = 20, Text = ">", TextAlign = ContentAlignment.MiddleCenter, ForeColor = ParseColor(profile.AccentColor) };
            runButton = new Button { Dock = DockStyle.Right, Width = 52, Text = "Run", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(49, 112, 143) };
            runButton.FlatAppearance.BorderSize = 0;
            input = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(24, 27, 33), ForeColor = Color.White, Font = new Font("Consolas", 10.0f) };
            commandBar.Controls.Add(input);
            commandBar.Controls.Add(runButton);
            commandBar.Controls.Add(prompt);

            Controls.Add(output);
            Controls.Add(commandBar);
            Controls.Add(header);

            runButton.Click += delegate { RunCurrentInput(); };
            restartButton.Click += delegate { Restart(); };
            input.KeyDown += InputKeyDown;
            header.Click += delegate { OnActivated(); };
            titleLabel.Click += delegate { OnActivated(); };
            output.Click += delegate { OnActivated(); };
            Click += delegate { OnActivated(); };
        }

        public void Start()
        {
            if (IsRunning) return;
            closing = false;
            try
            {
                Profile.Normalize();
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = Profile.ShellPath,
                    Arguments = "-NoLogo -NoProfile -NoExit -Command -",
                    WorkingDirectory = Profile.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process = new Process { StartInfo = info, EnableRaisingEvents = true };
                process.OutputDataReceived += ProcessOutput;
                process.ErrorDataReceived += ProcessError;
                process.Exited += ProcessExited;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                SetState("Running", Color.FromArgb(109, 213, 140));
                Append("PowerShellPlus session started · " + Profile.WorkingDirectory + Environment.NewLine, ParseColor(Profile.AccentColor));
                SendRaw("$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)");
                SendRaw("Write-Output ('PowerShell ' + $PSVersionTable.PSVersion + ' · ' + (Get-Location))");
                if (!String.IsNullOrWhiteSpace(Profile.StartupCommand)) SendRaw(Profile.StartupCommand);
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                Append("Could not start shell: " + ex.Message + Environment.NewLine, Color.FromArgb(255, 120, 120));
                SetState("Failed", Color.FromArgb(255, 120, 120));
                WorkspaceStore.Log("Session start failed for " + Profile.Name + ": " + ex);
                NotifyStateChanged();
            }
        }

        public void RunCommand(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return;
            if (!IsRunning) Start();
            if (!IsRunning) return;
            history.Add(command);
            historyIndex = history.Count;
            Append("> " + command + Environment.NewLine, Color.FromArgb(130, 200, 235));
            SendRaw(command);
        }

        private void SendRaw(string command)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.StandardInput.WriteLine(command);
                    process.StandardInput.Flush();
                }
            }
            catch (Exception ex)
            {
                Append("Command failed: " + ex.Message + Environment.NewLine, Color.FromArgb(255, 120, 120));
            }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        public void Stop()
        {
            closing = true;
            Process old = process;
            process = null;
            if (old != null)
            {
                try
                {
                    if (!old.HasExited)
                    {
                        old.StandardInput.WriteLine("exit");
                        old.StandardInput.Flush();
                        if (!old.WaitForExit(1200)) old.Kill();
                    }
                }
                catch { try { if (!old.HasExited) old.Kill(); } catch { } }
                old.Dispose();
            }
            SetState("Stopped", Color.Silver);
            NotifyStateChanged();
        }

        public void ClearOutput() { output.Clear(); }
        public void FocusCommand() { input.Focus(); }
        public string GetOutputText() { return output.Text; }

        public void SetFontSize(float size)
        {
            size = Math.Max(7.0f, Math.Min(24.0f, size));
            output.Font = new Font("Consolas", size);
            input.Font = new Font("Consolas", size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }

        private void RunCurrentInput()
        {
            string command = input.Text;
            input.Clear();
            RunCommand(command);
        }

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                RunCurrentInput(); e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up && history.Count > 0)
            {
                historyIndex = Math.Max(0, historyIndex - 1); input.Text = history[historyIndex]; input.SelectionStart = input.TextLength; e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && history.Count > 0)
            {
                historyIndex = Math.Min(history.Count, historyIndex + 1); input.Text = historyIndex == history.Count ? String.Empty : history[historyIndex]; input.SelectionStart = input.TextLength; e.SuppressKeyPress = true;
            }
        }

        private void ProcessOutput(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data + Environment.NewLine, output.ForeColor); }
        private void ProcessError(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data + Environment.NewLine, Color.FromArgb(255, 130, 130)); }

        private void ProcessExited(object sender, EventArgs e)
        {
            if (closing) return;
            SetState("Exited", Color.FromArgb(255, 190, 90));
            Append("[Session exited]" + Environment.NewLine, Color.FromArgb(255, 190, 90));
            NotifyStateChanged();
        }

        private void Append(string text, Color color)
        {
            if (output.IsDisposed) return;
            if (output.InvokeRequired) { try { output.BeginInvoke(new Action<string, Color>(Append), text, color); } catch { } return; }
            output.SelectionStart = output.TextLength;
            output.SelectionColor = color;
            output.AppendText(text);
            output.SelectionColor = output.ForeColor;
            if (output.TextLength > 750000) output.Text = output.Text.Substring(output.TextLength - 500000);
            output.SelectionStart = output.TextLength;
            output.ScrollToCaret();
        }

        private void SetState(string text, Color color)
        {
            if (stateLabel.IsDisposed) return;
            if (stateLabel.InvokeRequired) { try { stateLabel.BeginInvoke(new Action<string, Color>(SetState), text, color); } catch { } return; }
            stateLabel.Text = text;
            stateLabel.ForeColor = color;
        }

        private void OnActivated() { if (Activated != null) Activated(this, EventArgs.Empty); }
        private void NotifyStateChanged() { if (SessionStateChanged != null) SessionStateChanged(this, EventArgs.Empty); }
        private static bool SafeHasExited(Process value) { try { return value.HasExited; } catch { return true; } }
        private static Color ParseColor(string value) { try { return ColorTranslator.FromHtml(value); } catch { return Color.DeepSkyBlue; } }
    }
}
