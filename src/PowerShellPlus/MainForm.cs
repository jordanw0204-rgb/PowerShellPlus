using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PowerShellPlus
{
    public sealed class MainForm : Form
    {
        private WorkspaceState state;
        private string workspacePath;
        private readonly Dictionary<string, TerminalSessionControl> terminals = new Dictionary<string, TerminalSessionControl>();
        private readonly Panel canvas;
        private readonly ListBox sessionList;
        private readonly ListBox snippetList;
        private readonly ListBox automationList;
        private readonly ToolStripComboBox layoutCombo;
        private readonly ToolStripStatusLabel statusText;
        private readonly ToolStripStatusLabel sessionStatus;
        private readonly Timer automationTimer;
        private readonly Timer autosaveTimer;
        private readonly NotifyIcon trayIcon;
        private TerminalSessionControl selectedTerminal;
        private bool dirty;
        private bool exiting;

        public MainForm(WorkspaceState initialState, string initialPath)
        {
            state = initialState;
            workspacePath = initialPath;
            Text = "PowerShellPlus";
            Icon = SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 600);
            Size = new Size(1380, 820);
            BackColor = Color.FromArgb(27, 30, 36);
            Font = new Font("Segoe UI", 9.0f);
            KeyPreview = true;

            MenuStrip menu = BuildMenu();
            Controls.Add(menu);
            MainMenuStrip = menu;

            ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(240, 242, 246), Padding = new Padding(5, 3, 5, 3), Height = 34 };
            ToolStripButton newButton = new ToolStripButton("＋ Session") { ToolTipText = "New managed PowerShell session (Ctrl+N)" };
            ToolStripButton startButton = new ToolStripButton("▶ Start") { ToolTipText = "Start selected session" };
            ToolStripButton restartButton = new ToolStripButton("↻ Restart") { ToolTipText = "Restart selected session (F5)" };
            ToolStripButton runAllButton = new ToolStripButton("⇉ Send input to all") { ToolTipText = "Send the selected session's input to every running session (Ctrl+Shift+Enter)" };
            ToolStripButton clearButton = new ToolStripButton("Clear") { ToolTipText = "Clear selected output" };
            layoutCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
            layoutCombo.Items.AddRange(new object[] { "Grid", "Rows", "Columns", "Cascade" });
            layoutCombo.SelectedItem = state.Layout;
            if (layoutCombo.SelectedIndex < 0) layoutCombo.SelectedIndex = 0;
            newButton.Click += delegate { NewSession(); };
            startButton.Click += delegate { if (selectedTerminal != null) selectedTerminal.Start(); };
            restartButton.Click += delegate { if (selectedTerminal != null) selectedTerminal.Restart(); };
            runAllButton.Click += delegate { SendCurrentInputToAll(); };
            clearButton.Click += delegate { if (selectedTerminal != null) selectedTerminal.ClearOutput(); };
            layoutCombo.SelectedIndexChanged += delegate { state.Layout = (string)layoutCombo.SelectedItem; ApplyLayout(); MarkDirty("Layout changed"); };
            tools.Items.Add(newButton); tools.Items.Add(startButton); tools.Items.Add(restartButton); tools.Items.Add(new ToolStripSeparator()); tools.Items.Add(runAllButton); tools.Items.Add(clearButton); tools.Items.Add(new ToolStripSeparator()); tools.Items.Add(new ToolStripLabel("Layout")); tools.Items.Add(layoutCombo);
            Controls.Add(tools);

            StatusStrip status = new StatusStrip { BackColor = Color.FromArgb(240, 242, 246) };
            statusText = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            sessionStatus = new ToolStripStatusLabel("0 sessions");
            status.Items.Add(statusText); status.Items.Add(sessionStatus);
            Controls.Add(status);

            SplitContainer mainSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 292, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(52, 56, 66), IsSplitterFixed = false };
            mainSplit.Panel1.BackColor = Color.FromArgb(242, 244, 248);
            mainSplit.Panel2.BackColor = Color.FromArgb(24, 27, 32);
            Controls.Add(mainSplit);
            mainSplit.BringToFront();
            menu.BringToFront(); tools.BringToFront(); status.BringToFront();

            TabControl sidebar = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            TabPage sessionsTab = new TabPage("Sessions") { BackColor = Color.FromArgb(242, 244, 248), Padding = new Padding(8) };
            TabPage snippetsTab = new TabPage("Commands") { BackColor = Color.FromArgb(242, 244, 248), Padding = new Padding(8) };
            TabPage automationsTab = new TabPage("Automate") { BackColor = Color.FromArgb(242, 244, 248), Padding = new Padding(8) };
            sidebar.TabPages.Add(sessionsTab); sidebar.TabPages.Add(snippetsTab); sidebar.TabPages.Add(automationsTab);
            mainSplit.Panel1.Controls.Add(sidebar);

            sessionList = NewListBox();
            FlowLayoutPanel sessionButtons = NewButtonBar();
            sessionButtons.Controls.Add(NewSidebarButton("＋", "New session", delegate { NewSession(); }));
            sessionButtons.Controls.Add(NewSidebarButton("Edit", "Edit selected session", delegate { EditSelectedSession(); }));
            sessionButtons.Controls.Add(NewSidebarButton("Stop", "Stop selected session", delegate { if (selectedTerminal != null) selectedTerminal.Stop(); }));
            sessionButtons.Controls.Add(NewSidebarButton("×", "Close and remove selected session", delegate { RemoveSelectedSession(); }));
            sessionsTab.Controls.Add(sessionList); sessionsTab.Controls.Add(sessionButtons);
            sessionList.SelectedIndexChanged += SessionSelectionChanged;
            sessionList.DoubleClick += delegate { EditSelectedSession(); };

            snippetList = NewListBox();
            Label snippetHint = new Label { Dock = DockStyle.Top, Height = 48, Text = "Reusable commands. Double-click to run in the selected session.", ForeColor = Color.DimGray, Padding = new Padding(2, 4, 2, 4) };
            FlowLayoutPanel snippetButtons = NewButtonBar(72);
            snippetButtons.Controls.Add(NewSidebarButton("＋", "New command", delegate { NewSnippet(); }));
            snippetButtons.Controls.Add(NewSidebarButton("Edit", "Edit command", delegate { EditSnippet(); }));
            snippetButtons.Controls.Add(NewSidebarButton("Run", "Run in selected session", delegate { RunSnippet(false); }));
            snippetButtons.Controls.Add(NewSidebarButton("All", "Run in all sessions", delegate { RunSnippet(true); }));
            snippetButtons.Controls.Add(NewSidebarButton("×", "Delete command", delegate { RemoveSnippet(); }));
            snippetsTab.Controls.Add(snippetList); snippetsTab.Controls.Add(snippetButtons); snippetsTab.Controls.Add(snippetHint);
            snippetList.DoubleClick += delegate { RunSnippet(false); };

            automationList = NewListBox();
            Label automationHint = new Label { Dock = DockStyle.Top, Height = 58, Text = "Interval and daily jobs run while this app is open. Double-click to edit.", ForeColor = Color.DimGray, Padding = new Padding(2, 4, 2, 4) };
            FlowLayoutPanel automationButtons = NewButtonBar(72);
            automationButtons.Controls.Add(NewSidebarButton("＋", "New automation", delegate { NewAutomation(); }));
            automationButtons.Controls.Add(NewSidebarButton("Edit", "Edit automation", delegate { EditAutomation(); }));
            automationButtons.Controls.Add(NewSidebarButton("Run", "Run automation now", delegate { RunAutomationNow(); }));
            automationButtons.Controls.Add(NewSidebarButton("On/Off", "Toggle automation", delegate { ToggleAutomation(); }));
            automationButtons.Controls.Add(NewSidebarButton("×", "Delete automation", delegate { RemoveAutomation(); }));
            automationsTab.Controls.Add(automationList); automationsTab.Controls.Add(automationButtons); automationsTab.Controls.Add(automationHint);
            automationList.DoubleClick += delegate { EditAutomation(); };

            canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 22, 27), AutoScroll = false };
            mainSplit.Panel2.Controls.Add(canvas);
            canvas.Resize += delegate { ApplyLayout(); };

            automationTimer = new Timer { Interval = 5000 };
            automationTimer.Tick += delegate { CheckAutomations(); };
            automationTimer.Start();
            autosaveTimer = new Timer { Interval = 30000 };
            autosaveTimer.Tick += delegate { if (dirty) SaveWorkspace(false); };
            autosaveTimer.Start();

            trayIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "PowerShellPlus", Visible = true };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open PowerShellPlus", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("New session", null, delegate { ShowFromTray(); NewSession(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };

            FormClosing += MainFormClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) { Hide(); trayIcon.ShowBalloonTip(1200, "PowerShellPlus", "Still running your sessions and automations in the tray.", ToolTipIcon.Info); } };
            Shown += delegate { LoadWorkspaceIntoUi(); };
        }

        private MenuStrip BuildMenu()
        {
            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&New workspace", null, delegate { NewWorkspace(); });
            file.DropDownItems.Add("&Open workspace…", null, delegate { OpenWorkspace(); });
            ((ToolStripMenuItem)file.DropDownItems.Add("&Save", null, delegate { SaveWorkspace(true); })).ShortcutKeys = Keys.Control | Keys.S;
            file.DropDownItems.Add("Save &as…", null, delegate { SaveWorkspaceAs(); });
            file.DropDownItems.Add("Export workspace copy…", null, delegate { ExportWorkspace(); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Export selected output…", null, delegate { ExportSelectedOutput(); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, delegate { exiting = true; Close(); });

            ToolStripMenuItem session = new ToolStripMenuItem("&Session");
            ((ToolStripMenuItem)session.DropDownItems.Add("&New managed session", null, delegate { NewSession(); })).ShortcutKeys = Keys.Control | Keys.N;
            session.DropDownItems.Add("Open external PowerShell window", null, delegate { OpenExternalPowerShell(); });
            session.DropDownItems.Add(new ToolStripSeparator());
            session.DropDownItems.Add("Start selected", null, delegate { if (selectedTerminal != null) selectedTerminal.Start(); });
            ((ToolStripMenuItem)session.DropDownItems.Add("Restart selected", null, delegate { if (selectedTerminal != null) selectedTerminal.Restart(); })).ShortcutKeys = Keys.F5;
            session.DropDownItems.Add("Stop selected", null, delegate { if (selectedTerminal != null) selectedTerminal.Stop(); });
            session.DropDownItems.Add("Edit selected…", null, delegate { EditSelectedSession(); });
            ((ToolStripMenuItem)session.DropDownItems.Add("Close selected", null, delegate { RemoveSelectedSession(); })).ShortcutKeys = Keys.Control | Keys.W;

            ToolStripMenuItem layout = new ToolStripMenuItem("&Layout");
            foreach (string name in new[] { "Grid", "Rows", "Columns", "Cascade" })
            {
                string captured = name;
                layout.DropDownItems.Add(captured, null, delegate { layoutCombo.SelectedItem = captured; });
            }
            layout.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem external = new ToolStripMenuItem("Arrange external PowerShell windows");
            foreach (string name in new[] { "Grid", "Rows", "Columns", "Cascade" })
            {
                string captured = name;
                external.DropDownItems.Add(captured, null, delegate { ArrangeExternal(captured); });
            }
            layout.DropDownItems.Add(external);

            ToolStripMenuItem view = new ToolStripMenuItem("&View");
            ToolStripMenuItem alwaysTop = new ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = state.AlwaysOnTop };
            alwaysTop.CheckedChanged += delegate { state.AlwaysOnTop = alwaysTop.Checked; TopMost = alwaysTop.Checked; MarkDirty("View setting changed"); };
            view.DropDownItems.Add(alwaysTop);
            ((ToolStripMenuItem)view.DropDownItems.Add("Increase terminal font", null, delegate { ChangeFontSize(1); })).ShortcutKeys = Keys.Control | Keys.Oemplus;
            ((ToolStripMenuItem)view.DropDownItems.Add("Decrease terminal font", null, delegate { ChangeFontSize(-1); })).ShortcutKeys = Keys.Control | Keys.OemMinus;
            view.DropDownItems.Add("Clear selected output", null, delegate { if (selectedTerminal != null) selectedTerminal.ClearOutput(); });
            view.DropDownItems.Add("Clear all output", null, delegate { foreach (TerminalSessionControl terminal in terminals.Values) terminal.ClearOutput(); });
            view.DropDownItems.Add("Open log folder", null, delegate { Process.Start("explorer.exe", WorkspaceStore.AppDataDirectory); });

            ToolStripMenuItem help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("Keyboard shortcuts", null, delegate { ShowShortcuts(); });
            help.DropDownItems.Add("About PowerShellPlus", null, delegate { ShowAbout(); });
            menu.Items.Add(file); menu.Items.Add(session); menu.Items.Add(layout); menu.Items.Add(view); menu.Items.Add(help);
            return menu;
        }

        private void LoadWorkspaceIntoUi()
        {
            TopMost = state.AlwaysOnTop;
            Text = "PowerShellPlus — " + state.Name;
            RefreshLists();
            foreach (SessionProfile profile in state.Sessions) AddTerminal(profile, profile.AutoStart);
            if (state.Sessions.Count == 0) statusText.Text = "Create a session to get started";
            else SelectSession(state.Sessions[0].Id);
            ApplyLayout(); UpdateSessionStatus();
        }

        private void AddTerminal(SessionProfile profile, bool start)
        {
            TerminalSessionControl terminal = new TerminalSessionControl(profile) { Visible = true };
            terminal.Activated += delegate { SelectSession(profile.Id); };
            terminal.SessionStateChanged += delegate { UpdateSessionStatus(); };
            terminals[profile.Id] = terminal;
            canvas.Controls.Add(terminal);
            if (start) terminal.Start();
            ApplyLayout();
        }

        private void NewSession()
        {
            using (SessionDialog dialog = new SessionDialog(null))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                state.Sessions.Add(dialog.Result); AddTerminal(dialog.Result, dialog.Result.AutoStart); RefreshLists(); SelectSession(dialog.Result.Id); MarkDirty("Session created");
            }
        }

        private void EditSelectedSession()
        {
            SessionProfile existing = SelectedProfile(); if (existing == null) return;
            using (SessionDialog dialog = new SessionDialog(existing))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int index = state.Sessions.IndexOf(existing); bool wasRunning = selectedTerminal != null && selectedTerminal.IsRunning;
                if (selectedTerminal != null) { canvas.Controls.Remove(selectedTerminal); selectedTerminal.Dispose(); terminals.Remove(existing.Id); }
                state.Sessions[index] = dialog.Result; AddTerminal(dialog.Result, wasRunning || dialog.Result.AutoStart); RefreshLists(); SelectSession(dialog.Result.Id); MarkDirty("Session updated");
            }
        }

        private void RemoveSelectedSession()
        {
            SessionProfile profile = SelectedProfile(); if (profile == null) return;
            if (MessageBox.Show(this, "Close and remove '" + profile.Name + "' from this workspace?", "PowerShellPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TerminalSessionControl terminal;
            if (terminals.TryGetValue(profile.Id, out terminal)) { canvas.Controls.Remove(terminal); terminal.Dispose(); terminals.Remove(profile.Id); }
            state.Sessions.Remove(profile); selectedTerminal = null; RefreshLists(); if (state.Sessions.Count > 0) SelectSession(state.Sessions[0].Id); ApplyLayout(); UpdateSessionStatus(); MarkDirty("Session removed");
        }

        private void SessionSelectionChanged(object sender, EventArgs e)
        {
            SessionProfile profile = sessionList.SelectedItem as SessionProfile;
            if (profile == null) return;
            TerminalSessionControl terminal;
            if (terminals.TryGetValue(profile.Id, out terminal))
            {
                selectedTerminal = terminal;
                if (state.Layout == "Cascade") terminal.BringToFront();
                statusText.Text = profile.Name + " · " + (terminal.IsRunning ? "Running" : "Stopped");
            }
        }

        private void SelectSession(string id)
        {
            for (int i = 0; i < sessionList.Items.Count; i++)
            {
                SessionProfile profile = (SessionProfile)sessionList.Items[i];
                if (profile.Id == id) { sessionList.SelectedIndex = i; break; }
            }
        }

        private SessionProfile SelectedProfile() { return sessionList.SelectedItem as SessionProfile; }

        private void ApplyLayout()
        {
            if (canvas == null || terminals.Count == 0) return;
            List<TerminalSessionControl> ordered = new List<TerminalSessionControl>();
            foreach (SessionProfile profile in state.Sessions) { TerminalSessionControl terminal; if (terminals.TryGetValue(profile.Id, out terminal)) ordered.Add(terminal); }
            Rectangle[] bounds = PowerShellPlus.LayoutEngine.Calculate(state.Layout, canvas.ClientRectangle, ordered.Count);
            canvas.SuspendLayout();
            for (int i = 0; i < ordered.Count; i++) ordered[i].Bounds = bounds[i];
            if (state.Layout == "Cascade" && selectedTerminal != null) selectedTerminal.BringToFront();
            canvas.ResumeLayout();
        }

        private void SendCurrentInputToAll()
        {
            if (selectedTerminal == null) return;
            string command = selectedTerminal.CurrentInput;
            if (String.IsNullOrWhiteSpace(command)) { statusText.Text = "Type a command in the selected session first"; selectedTerminal.FocusCommand(); return; }
            foreach (TerminalSessionControl terminal in terminals.Values) terminal.RunCommand(command);
            selectedTerminal.CurrentInput = String.Empty; statusText.Text = "Command sent to " + terminals.Count + " sessions";
        }

        private void NewSnippet()
        {
            using (SnippetDialog dialog = new SnippetDialog(null)) if (dialog.ShowDialog(this) == DialogResult.OK) { state.Snippets.Add(dialog.Result); RefreshLists(); snippetList.SelectedItem = dialog.Result; MarkDirty("Command saved"); }
        }

        private void EditSnippet()
        {
            CommandSnippet existing = snippetList.SelectedItem as CommandSnippet; if (existing == null) return;
            using (SnippetDialog dialog = new SnippetDialog(existing)) if (dialog.ShowDialog(this) == DialogResult.OK) { int index = state.Snippets.IndexOf(existing); state.Snippets[index] = dialog.Result; RefreshLists(); snippetList.SelectedItem = dialog.Result; MarkDirty("Command updated"); }
        }

        private void RemoveSnippet()
        {
            CommandSnippet value = snippetList.SelectedItem as CommandSnippet; if (value == null) return;
            if (MessageBox.Show(this, "Delete command '" + value.Name + "'?", "PowerShellPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { state.Snippets.Remove(value); RefreshLists(); MarkDirty("Command deleted"); }
        }

        private void RunSnippet(bool all)
        {
            CommandSnippet snippet = snippetList.SelectedItem as CommandSnippet; if (snippet == null) return;
            if (all) foreach (TerminalSessionControl terminal in terminals.Values) terminal.RunCommand(snippet.Command);
            else if (selectedTerminal != null) selectedTerminal.RunCommand(snippet.Command);
            else { statusText.Text = "Select a session first"; return; }
            statusText.Text = "Ran '" + snippet.Name + "'" + (all ? " in all sessions" : String.Empty);
        }

        private void NewAutomation()
        {
            using (AutomationDialog dialog = new AutomationDialog(null, state.Sessions)) if (dialog.ShowDialog(this) == DialogResult.OK) { state.Automations.Add(dialog.Result); RefreshLists(); automationList.SelectedItem = dialog.Result; MarkDirty("Automation created"); }
        }

        private void EditAutomation()
        {
            AutomationRule existing = automationList.SelectedItem as AutomationRule; if (existing == null) return;
            using (AutomationDialog dialog = new AutomationDialog(existing, state.Sessions)) if (dialog.ShowDialog(this) == DialogResult.OK) { int index = state.Automations.IndexOf(existing); state.Automations[index] = dialog.Result; RefreshLists(); automationList.SelectedItem = dialog.Result; MarkDirty("Automation updated"); }
        }

        private void RemoveAutomation()
        {
            AutomationRule value = automationList.SelectedItem as AutomationRule; if (value == null) return;
            if (MessageBox.Show(this, "Delete automation '" + value.Name + "'?", "PowerShellPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { state.Automations.Remove(value); RefreshLists(); MarkDirty("Automation deleted"); }
        }

        private void ToggleAutomation()
        {
            AutomationRule value = automationList.SelectedItem as AutomationRule; if (value == null) return;
            value.Enabled = !value.Enabled; RefreshLists(); automationList.SelectedItem = value; MarkDirty(value.Enabled ? "Automation enabled" : "Automation disabled");
        }

        private void RunAutomationNow()
        {
            AutomationRule value = automationList.SelectedItem as AutomationRule; if (value == null) return;
            ExecuteAutomation(value); value.LastRunUtc = DateTime.UtcNow; RefreshLists(); automationList.SelectedItem = value; MarkDirty("Automation ran");
        }

        private void CheckAutomations()
        {
            bool ran = false;
            foreach (AutomationRule rule in state.Automations)
            {
                if (!rule.IsDue(DateTime.UtcNow, DateTime.Now)) continue;
                ExecuteAutomation(rule); rule.LastRunUtc = DateTime.UtcNow; ran = true;
            }
            if (ran) { RefreshLists(); MarkDirty("Scheduled automation ran"); SaveWorkspace(false); }
        }

        private void ExecuteAutomation(AutomationRule rule)
        {
            int count = 0;
            foreach (KeyValuePair<string, TerminalSessionControl> entry in terminals)
            {
                if (rule.TargetSessionId == "*" || rule.TargetSessionId == entry.Key) { entry.Value.RunCommand(rule.Command); count++; }
            }
            statusText.Text = count > 0 ? "Automation '" + rule.Name + "' ran in " + count + " session(s)" : "Automation '" + rule.Name + "' has no available target";
            WorkspaceStore.Log(statusText.Text);
        }

        private void RefreshLists()
        {
            string sessionId = (sessionList == null || sessionList.SelectedItem == null) ? null : ((SessionProfile)sessionList.SelectedItem).Id;
            string snippetId = (snippetList == null || snippetList.SelectedItem == null) ? null : ((CommandSnippet)snippetList.SelectedItem).Id;
            string automationId = (automationList == null || automationList.SelectedItem == null) ? null : ((AutomationRule)automationList.SelectedItem).Id;
            if (sessionList != null) { sessionList.Items.Clear(); foreach (SessionProfile item in state.Sessions) sessionList.Items.Add(item); SelectListItem(sessionList, sessionId); }
            if (snippetList != null) { snippetList.Items.Clear(); foreach (CommandSnippet item in state.Snippets.OrderBy(x => x.Category).ThenBy(x => x.Name)) snippetList.Items.Add(item); SelectListItem(snippetList, snippetId); }
            if (automationList != null) { automationList.Items.Clear(); foreach (AutomationRule item in state.Automations) automationList.Items.Add(item); SelectListItem(automationList, automationId); }
        }

        private static void SelectListItem(ListBox list, string id)
        {
            if (id == null) return;
            for (int i = 0; i < list.Items.Count; i++)
            {
                object item = list.Items[i]; string itemId = item is SessionProfile ? ((SessionProfile)item).Id : item is CommandSnippet ? ((CommandSnippet)item).Id : ((AutomationRule)item).Id;
                if (itemId == id) { list.SelectedIndex = i; return; }
            }
        }

        private void NewWorkspace()
        {
            if (!ConfirmReplaceWorkspace()) return;
            ReplaceWorkspace(WorkspaceState.CreateDefault(), WorkspaceStore.DefaultPath);
        }

        private void OpenWorkspace()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "PowerShellPlus workspace|*.json|All files|*.*", InitialDirectory = WorkspaceStore.AppDataDirectory })
                if (dialog.ShowDialog(this) == DialogResult.OK && ConfirmReplaceWorkspace()) ReplaceWorkspace(WorkspaceStore.Load(dialog.FileName), dialog.FileName);
        }

        private bool ConfirmReplaceWorkspace()
        {
            if (!dirty) return true;
            DialogResult answer = MessageBox.Show(this, "Save the current workspace before continuing?", "PowerShellPlus", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) SaveWorkspace(false);
            return true;
        }

        private void ReplaceWorkspace(WorkspaceState replacement, string path)
        {
            foreach (TerminalSessionControl terminal in terminals.Values) { canvas.Controls.Remove(terminal); terminal.Dispose(); }
            terminals.Clear(); selectedTerminal = null; state = replacement; workspacePath = path; dirty = false; layoutCombo.SelectedItem = state.Layout; LoadWorkspaceIntoUi();
        }

        private void SaveWorkspace(bool announce)
        {
            try { WorkspaceStore.Save(state, workspacePath); dirty = false; Text = "PowerShellPlus — " + state.Name; statusText.Text = announce ? "Workspace saved" : "Autosaved " + DateTime.Now.ToString("h:mm:ss tt"); }
            catch (Exception ex) { statusText.Text = "Save failed"; WorkspaceStore.Log("Save failed: " + ex); if (announce) MessageBox.Show(this, "Could not save the workspace:\n" + ex.Message, "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SaveWorkspaceAs()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "PowerShellPlus workspace|*.json", FileName = state.Name.Replace(" ", "-") + ".json" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { workspacePath = dialog.FileName; SaveWorkspace(true); }
        }

        private void ExportWorkspace()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "PowerShellPlus workspace|*.json", FileName = state.Name.Replace(" ", "-") + "-copy.json" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { WorkspaceStore.Save(state, dialog.FileName); statusText.Text = "Workspace copy exported"; }
        }

        private void ExportSelectedOutput()
        {
            if (selectedTerminal == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Text file|*.txt|Log file|*.log|All files|*.*", FileName = SelectedProfile().Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(dialog.FileName, selectedTerminal.GetOutputText()); statusText.Text = "Output exported"; }
        }

        private void ArrangeExternal(string layout)
        {
            int count = NativeConsoleWindowManager.Arrange(layout);
            statusText.Text = count == 0 ? "No external PowerShell windows found" : "Arranged " + count + " external PowerShell window(s)";
        }

        private void OpenExternalPowerShell()
        {
            SessionProfile profile = SelectedProfile() ?? new SessionProfile();
            try { Process.Start(new ProcessStartInfo { FileName = profile.ShellPath, Arguments = "-NoExit", WorkingDirectory = profile.WorkingDirectory, UseShellExecute = true }); statusText.Text = "Opened external PowerShell window"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open PowerShell", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ChangeFontSize(int delta)
        {
            foreach (TerminalSessionControl terminal in terminals.Values)
            {
                float current = terminal.Controls.OfType<RichTextBox>().Select(x => x.Font.Size).FirstOrDefault();
                terminal.SetFontSize((current <= 0 ? 10 : current) + delta);
            }
        }

        private void MarkDirty(string message) { dirty = true; statusText.Text = message + " · unsaved"; }
        private void UpdateSessionStatus() { sessionStatus.Text = terminals.Values.Count(x => x.IsRunning) + " running / " + terminals.Count + " sessions"; }
        private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        internal void ExitForSmokeTest()
        {
            exiting = true;
            Close();
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult answer = MessageBox.Show(this, "Exit PowerShellPlus and stop all managed sessions?\n\nChoose No to keep it running in the system tray.", "PowerShellPlus", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
                if (answer == DialogResult.No) { e.Cancel = true; Hide(); return; }
            }
            SaveWorkspace(false); automationTimer.Stop(); autosaveTimer.Stop(); trayIcon.Visible = false;
            foreach (TerminalSessionControl terminal in terminals.Values) terminal.Stop();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.Enter)) { SendCurrentInputToAll(); return true; }
            if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D9))
            {
                int index = (int)(keyData & Keys.KeyCode) - (int)Keys.D1; if (index < sessionList.Items.Count) { sessionList.SelectedIndex = index; if (selectedTerminal != null) selectedTerminal.FocusCommand(); } return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowShortcuts()
        {
            MessageBox.Show(this, "Ctrl+N    New session\nCtrl+S    Save workspace\nCtrl+W    Close selected session\nF5            Restart selected session\nCtrl+Shift+Enter    Send current input to every session\nCtrl+1…9    Focus session\nCtrl++ / Ctrl+-    Terminal font size\nUp / Down    Command history", "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(this, "PowerShellPlus 1.0\n\nA desktop workspace for running, arranging, and automating multiple PowerShell sessions.\n\nWorkspace: " + workspacePath + "\nLog: " + WorkspaceStore.LogPath, "About PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static ListBox NewListBox()
        {
            return new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Segoe UI", 9.5f), ItemHeight = 28, BackColor = Color.White };
        }

        private static FlowLayoutPanel NewButtonBar(int height)
        {
            return new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = height, Padding = new Padding(0, 7, 0, 0), WrapContents = true, BackColor = Color.FromArgb(242, 244, 248) };
        }
        private static FlowLayoutPanel NewButtonBar() { return NewButtonBar(48); }

        private static Button NewSidebarButton(string text, string tip, EventHandler handler)
        {
            Button button = new Button { Text = text, AutoSize = true, Height = 30, FlatStyle = FlatStyle.System };
            button.Click += handler; new ToolTip().SetToolTip(button, tip); return button;
        }
    }
}
