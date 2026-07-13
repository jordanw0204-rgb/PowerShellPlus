using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PowerShellPlus
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
                return RunSelfTests(args.Length > 1 ? args[1] : null);

            if (args.Length > 0 && args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
                return RunSmokeTest(args.Length > 1 ? args[1] : null);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string path = args.Length > 1 && args[0] == "--workspace" ? Path.GetFullPath(args[1]) : WorkspaceStore.DefaultPath;
                WorkspaceState state = WorkspaceStore.Load(path);
                Application.Run(new MainForm(state, path));
                return 0;
            }
            catch (Exception ex)
            {
                WorkspaceStore.Log("Fatal application error: " + ex);
                MessageBox.Show("PowerShellPlus could not start:\n\n" + ex.Message + "\n\nSee " + WorkspaceStore.LogPath, "PowerShellPlus", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int RunSmokeTest(string reportPath)
        {
            string workspace = Path.Combine(Path.GetTempPath(), "PowerShellPlus-Smoke-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm form = new MainForm(WorkspaceState.CreateDefault(), workspace);
                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1800 };
                timer.Tick += delegate
                {
                    timer.Stop();
                    form.ExitForSmokeTest();
                };
                timer.Start();
                Application.Run(form);
                bool saved = File.Exists(workspace);
                if (!String.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, saved ? "PASS Deployed GUI opened, initialized, saved, and exited cleanly." : "FAIL GUI did not save its smoke-test workspace.");
                return saved ? 0 : 3;
            }
            catch (Exception ex)
            {
                if (!String.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, "FAIL " + ex);
                return 4;
            }
            finally
            {
                try { if (File.Exists(workspace)) File.Delete(workspace); } catch { }
                try { if (File.Exists(workspace + ".bak")) File.Delete(workspace + ".bak"); } catch { }
            }
        }

        private static int RunSelfTests(string reportPath)
        {
            StringBuilder report = new StringBuilder();
            int failures = 0;
            Action<bool, string> check = delegate(bool condition, string name)
            {
                report.AppendLine((condition ? "PASS " : "FAIL ") + name);
                if (!condition) failures++;
            };

            try
            {
                Rectangle area = new Rectangle(0, 0, 1200, 800);
                foreach (string layout in new[] { "Grid", "Rows", "Columns", "Cascade" })
                {
                    Rectangle[] cells = LayoutEngine.Calculate(layout, area, 5);
                    check(cells.Length == 5, layout + " creates one rectangle per session");
                    bool valid = true;
                    foreach (Rectangle cell in cells) valid = valid && cell.Width > 0 && cell.Height > 0 && area.Contains(cell);
                    check(valid, layout + " rectangles stay inside the workspace");
                }

                AutomationRule interval = new AutomationRule { Command = "Get-Date", ScheduleType = "Interval", IntervalMinutes = 5, LastRunUtc = DateTime.UtcNow.AddMinutes(-6) };
                check(interval.IsDue(DateTime.UtcNow, DateTime.Now), "Interval automation becomes due");
                interval.LastRunUtc = DateTime.UtcNow;
                check(!interval.IsDue(DateTime.UtcNow, DateTime.Now), "Interval automation does not run twice");

                AutomationRule daily = new AutomationRule { Command = "Get-Date", ScheduleType = "Daily", DailyTime = DateTime.Now.AddMinutes(-1).ToString("HH:mm"), LastRunUtc = DateTime.UtcNow.AddDays(-1) };
                check(daily.IsDue(DateTime.UtcNow, DateTime.Now), "Daily automation becomes due once per day");
                daily.LastRunUtc = DateTime.UtcNow;
                check(!daily.IsDue(DateTime.UtcNow, DateTime.Now), "Daily automation does not repeat the same day");

                string tempDirectory = Path.Combine(Path.GetTempPath(), "PowerShellPlus-SelfTest-" + Guid.NewGuid().ToString("N"));
                string tempPath = Path.Combine(tempDirectory, "workspace.json");
                WorkspaceState original = WorkspaceState.CreateDefault();
                original.Name = "Self Test";
                WorkspaceStore.Save(original, tempPath);
                WorkspaceState loaded = WorkspaceStore.Load(tempPath);
                check(loaded.Name == original.Name && loaded.Sessions.Count == original.Sessions.Count && loaded.Snippets.Count == original.Snippets.Count, "Workspace JSON round-trip");
                try { Directory.Delete(tempDirectory, true); } catch { }

                string shell = ShellLocator.FindBestShell();
                check(!String.IsNullOrWhiteSpace(shell) && File.Exists(shell), "A PowerShell executable is available");

                if (File.Exists(shell))
                {
                    SessionProfile profile = new SessionProfile
                    {
                        Name = "Integration Test",
                        ShellPath = shell,
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        AutoStart = false
                    };
                    using (Form host = new Form())
                    using (TerminalSessionControl terminal = new TerminalSessionControl(profile))
                    {
                        host.Controls.Add(terminal);
                        host.CreateControl();
                        terminal.CreateControl();
                        terminal.Start();
                        terminal.RunCommand("Write-Output ('PSPLUS_' + 'INTEGRATION_OUTPUT')");
                        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
                        while (DateTime.UtcNow < deadline && terminal.GetOutputText().IndexOf("PSPLUS_INTEGRATION_OUTPUT", StringComparison.Ordinal) < 0)
                        {
                            Application.DoEvents();
                            Thread.Sleep(50);
                        }
                        check(terminal.IsRunning, "Managed PowerShell process stays running");
                        check(terminal.GetOutputText().IndexOf("PSPLUS_INTEGRATION_OUTPUT", StringComparison.Ordinal) >= 0, "Managed session executes a command and captures output");
                        terminal.Stop();
                        check(!terminal.IsRunning, "Managed PowerShell process stops cleanly");
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("FAIL Unexpected exception: " + ex);
            }

            report.AppendLine();
            report.AppendLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            if (!String.IsNullOrWhiteSpace(reportPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(reportPath, report.ToString());
            }
            return failures == 0 ? 0 : 2;
        }
    }
}
