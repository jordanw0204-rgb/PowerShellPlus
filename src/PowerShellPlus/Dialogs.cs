using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PowerShellPlus
{
    internal static class DialogStyle
    {
        public static void Apply(Form form, string title, Size size)
        {
            form.Text = title;
            form.Size = size;
            form.StartPosition = FormStartPosition.CenterParent;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.Font = new Font("Segoe UI", 9.5f);
            form.BackColor = Color.FromArgb(245, 247, 250);
        }

        public static Label Label(string text, int x, int y, int width)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, 22), ForeColor = Color.FromArgb(50, 55, 65) };
        }

        public static Button Button(string text, int x, int y, int width, DialogResult result)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 32), DialogResult = result, FlatStyle = FlatStyle.System };
        }
    }

    public sealed class SessionDialog : Form
    {
        private readonly TextBox nameBox;
        private readonly TextBox shellBox;
        private readonly TextBox directoryBox;
        private readonly TextBox startupBox;
        private readonly CheckBox autoStartBox;
        private readonly Button colorButton;
        private string accentColor;
        public SessionProfile Result { get; private set; }

        public SessionDialog(SessionProfile source)
        {
            DialogStyle.Apply(this, source == null ? "New PowerShell session" : "Edit session", new Size(590, 445));
            SessionProfile profile = source == null ? new SessionProfile() : WorkspaceStore.Clone(new WorkspaceState { Sessions = new List<SessionProfile> { source } }).Sessions[0];
            accentColor = profile.AccentColor;

            Controls.Add(DialogStyle.Label("Session name", 22, 22, 160));
            nameBox = new TextBox { Location = new Point(22, 46), Width = 530, Text = profile.Name };
            Controls.Add(nameBox);

            Controls.Add(DialogStyle.Label("PowerShell executable", 22, 82, 200));
            shellBox = new TextBox { Location = new Point(22, 106), Width = 440, Text = profile.ShellPath };
            Button shellBrowse = new Button { Text = "Browse…", Location = new Point(470, 104), Size = new Size(82, 28) };
            shellBrowse.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "PowerShell|powershell.exe;pwsh.exe|Applications|*.exe|All files|*.*", FileName = shellBox.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) shellBox.Text = dialog.FileName;
            };
            Controls.Add(shellBox); Controls.Add(shellBrowse);

            Controls.Add(DialogStyle.Label("Working directory", 22, 142, 200));
            directoryBox = new TextBox { Location = new Point(22, 166), Width = 440, Text = profile.WorkingDirectory };
            Button dirBrowse = new Button { Text = "Browse…", Location = new Point(470, 164), Size = new Size(82, 28) };
            dirBrowse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { SelectedPath = directoryBox.Text, Description = "Choose the starting directory" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) directoryBox.Text = dialog.SelectedPath;
            };
            Controls.Add(directoryBox); Controls.Add(dirBrowse);

            Controls.Add(DialogStyle.Label("Startup command (optional)", 22, 202, 220));
            startupBox = new TextBox { Location = new Point(22, 226), Width = 530, Height = 62, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = profile.StartupCommand, Font = new Font("Consolas", 9.5f) };
            Controls.Add(startupBox);

            autoStartBox = new CheckBox { Location = new Point(22, 304), Width = 260, Text = "Start this session with the workspace", Checked = profile.AutoStart };
            colorButton = new Button { Location = new Point(382, 298), Size = new Size(170, 31), Text = "Choose accent color…", BackColor = ParseColor(accentColor) };
            colorButton.Click += delegate
            {
                using (ColorDialog dialog = new ColorDialog { Color = ParseColor(accentColor), FullOpen = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { accentColor = ColorTranslator.ToHtml(dialog.Color); colorButton.BackColor = dialog.Color; }
            };
            Controls.Add(autoStartBox); Controls.Add(colorButton);

            Button cancel = DialogStyle.Button("Cancel", 366, 350, 88, DialogResult.Cancel);
            Button save = DialogStyle.Button(source == null ? "Create" : "Save", 464, 350, 88, DialogResult.None);
            save.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(nameBox.Text)) { MessageBox.Show(this, "Give the session a name.", "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!File.Exists(shellBox.Text)) { MessageBox.Show(this, "The selected PowerShell executable does not exist.", "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (!Directory.Exists(directoryBox.Text)) { MessageBox.Show(this, "The working directory does not exist.", "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                profile.Name = nameBox.Text.Trim(); profile.ShellPath = shellBox.Text.Trim(); profile.WorkingDirectory = directoryBox.Text.Trim(); profile.StartupCommand = startupBox.Text; profile.AccentColor = accentColor; profile.AutoStart = autoStartBox.Checked;
                Result = profile; DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(cancel); Controls.Add(save); AcceptButton = save; CancelButton = cancel;
        }

        private static Color ParseColor(string value) { try { return ColorTranslator.FromHtml(value); } catch { return Color.DeepSkyBlue; } }
    }

    public sealed class SnippetDialog : Form
    {
        private readonly TextBox nameBox;
        private readonly TextBox categoryBox;
        private readonly TextBox commandBox;
        public CommandSnippet Result { get; private set; }

        public SnippetDialog(CommandSnippet source)
        {
            DialogStyle.Apply(this, source == null ? "New command snippet" : "Edit command snippet", new Size(600, 390));
            CommandSnippet value = source == null ? new CommandSnippet() : new CommandSnippet { Id = source.Id, Name = source.Name, Category = source.Category, Command = source.Command };
            Controls.Add(DialogStyle.Label("Name", 22, 20, 120));
            nameBox = new TextBox { Location = new Point(22, 44), Width = 350, Text = value.Name };
            Controls.Add(nameBox);
            Controls.Add(DialogStyle.Label("Category", 390, 20, 160));
            categoryBox = new TextBox { Location = new Point(390, 44), Width = 166, Text = value.Category };
            Controls.Add(categoryBox);
            Controls.Add(DialogStyle.Label("PowerShell command", 22, 82, 200));
            commandBox = new TextBox { Location = new Point(22, 106), Width = 534, Height = 168, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, Font = new Font("Consolas", 10.0f), Text = value.Command };
            Controls.Add(commandBox);
            Button cancel = DialogStyle.Button("Cancel", 370, 298, 88, DialogResult.Cancel);
            Button save = DialogStyle.Button("Save", 468, 298, 88, DialogResult.None);
            save.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(nameBox.Text) || String.IsNullOrWhiteSpace(commandBox.Text)) { MessageBox.Show(this, "Name and command are required.", "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                value.Name = nameBox.Text.Trim(); value.Category = String.IsNullOrWhiteSpace(categoryBox.Text) ? "General" : categoryBox.Text.Trim(); value.Command = commandBox.Text.Trim(); Result = value; DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(cancel); Controls.Add(save); AcceptButton = save; CancelButton = cancel;
        }
    }

    public sealed class AutomationDialog : Form
    {
        private sealed class TargetItem
        {
            public string Id; public string Name;
            public override string ToString() { return Name; }
        }

        private readonly TextBox nameBox;
        private readonly TextBox commandBox;
        private readonly ComboBox targetBox;
        private readonly ComboBox typeBox;
        private readonly NumericUpDown intervalBox;
        private readonly DateTimePicker timePicker;
        private readonly CheckBox enabledBox;
        public AutomationRule Result { get; private set; }

        public AutomationDialog(AutomationRule source, IList<SessionProfile> sessions)
        {
            DialogStyle.Apply(this, source == null ? "New automation" : "Edit automation", new Size(620, 495));
            AutomationRule value = source == null ? new AutomationRule() : new AutomationRule { Id = source.Id, Name = source.Name, Command = source.Command, TargetSessionId = source.TargetSessionId, ScheduleType = source.ScheduleType, IntervalMinutes = source.IntervalMinutes, DailyTime = source.DailyTime, Enabled = source.Enabled, LastRunUtc = source.LastRunUtc };
            Controls.Add(DialogStyle.Label("Name", 22, 18, 180));
            nameBox = new TextBox { Location = new Point(22, 42), Width = 558, Text = value.Name };
            Controls.Add(nameBox);
            Controls.Add(DialogStyle.Label("PowerShell command", 22, 76, 220));
            commandBox = new TextBox { Location = new Point(22, 100), Width = 558, Height = 105, Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9.5f), Text = value.Command };
            Controls.Add(commandBox);
            Controls.Add(DialogStyle.Label("Target", 22, 220, 170));
            targetBox = new ComboBox { Location = new Point(22, 244), Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            targetBox.Items.Add(new TargetItem { Id = "*", Name = "All running sessions" });
            foreach (SessionProfile session in sessions) targetBox.Items.Add(new TargetItem { Id = session.Id, Name = session.Name });
            for (int i = 0; i < targetBox.Items.Count; i++) if (((TargetItem)targetBox.Items[i]).Id == value.TargetSessionId) targetBox.SelectedIndex = i;
            if (targetBox.SelectedIndex < 0) targetBox.SelectedIndex = 0;
            Controls.Add(targetBox);
            Controls.Add(DialogStyle.Label("Schedule", 302, 220, 110));
            typeBox = new ComboBox { Location = new Point(302, 244), Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            typeBox.Items.AddRange(new object[] { "Interval", "Daily" }); typeBox.SelectedItem = value.ScheduleType;
            Controls.Add(typeBox);
            intervalBox = new NumericUpDown { Location = new Point(444, 244), Width = 136, Minimum = 1, Maximum = 10080, Value = Math.Max(1, Math.Min(10080, value.IntervalMinutes)) };
            timePicker = new DateTimePicker { Location = new Point(444, 244), Width = 136, Format = DateTimePickerFormat.Custom, CustomFormat = "hh:mm tt", ShowUpDown = true };
            TimeSpan parsed; if (TimeSpan.TryParse(value.DailyTime, out parsed)) timePicker.Value = DateTime.Today.Add(parsed);
            Controls.Add(intervalBox); Controls.Add(timePicker);
            Label intervalHint = new Label { Location = new Point(444, 273), Width = 136, Text = "minutes", ForeColor = Color.DimGray };
            Controls.Add(intervalHint);
            Action refreshSchedule = delegate { bool daily = (string)typeBox.SelectedItem == "Daily"; timePicker.Visible = daily; intervalBox.Visible = !daily; intervalHint.Visible = !daily; };
            typeBox.SelectedIndexChanged += delegate { refreshSchedule(); }; refreshSchedule();
            enabledBox = new CheckBox { Location = new Point(22, 302), Width = 300, Text = "Enabled while PowerShellPlus is running", Checked = value.Enabled };
            Controls.Add(enabledBox);
            Label note = new Label { Location = new Point(22, 332), Size = new Size(558, 42), Text = "Automations are persisted with the workspace. Commands are sent to live managed sessions and the last-run time is saved automatically.", ForeColor = Color.DimGray };
            Controls.Add(note);
            Button cancel = DialogStyle.Button("Cancel", 394, 390, 88, DialogResult.Cancel);
            Button save = DialogStyle.Button("Save", 492, 390, 88, DialogResult.None);
            save.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(nameBox.Text) || String.IsNullOrWhiteSpace(commandBox.Text)) { MessageBox.Show(this, "Name and command are required.", "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                TargetItem target = (TargetItem)targetBox.SelectedItem; value.Name = nameBox.Text.Trim(); value.Command = commandBox.Text.Trim(); value.TargetSessionId = target.Id; value.ScheduleType = (string)typeBox.SelectedItem; value.IntervalMinutes = (int)intervalBox.Value; value.DailyTime = timePicker.Value.ToString("HH:mm"); value.Enabled = enabledBox.Checked;
                Result = value; DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(cancel); Controls.Add(save); AcceptButton = save; CancelButton = cancel;
        }
    }
}
