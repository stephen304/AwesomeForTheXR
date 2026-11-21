using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AwesomeForTheXR.Patcher.Core;

namespace AwesomeForTheXR.Patcher.WinForms
{
    internal sealed class MainForm : Form
    {
        private readonly PatcherService service;
        private readonly TextBox folder = new TextBox();
        private readonly Button browse = new Button { Text = "&Browse…", AutoSize = true };
        private readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill };
        private readonly Button patch = new Button { Text = "&Patch", Width = 90, AutoSize = true };
        private readonly Button revert = new Button { Text = "&Revert", Width = 90, AutoSize = true };
        private readonly Timer debounce = new Timer { Interval = 300 };
        private int selectionVersion;
        private bool changingFiles;

        internal MainForm(PatcherService service, string selectedFolder)
        {
            this.service = service;
            Text = "AwesomeForTheXR Patcher";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(600, 210);
            MinimumSize = new Size(420, 245);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 4
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var label = new Label { Text = "&Game folder", AutoSize = true };
            folder.AccessibleName = "Game folder";
            folder.Dock = DockStyle.Fill;
            status.Name = "FileStatus";
            status.AccessibleDescription = "Verification result for the selected game folder";
            status.UseMnemonic = false;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "Looking for the game folder…";
            var actions = new FlowLayoutPanel
            {
                AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight
            };
            actions.Controls.Add(patch);
            actions.Controls.Add(revert);
            layout.Controls.Add(label, 0, 0);
            layout.Controls.Add(folder, 0, 1);
            layout.Controls.Add(browse, 1, 1);
            layout.Controls.Add(status, 0, 2);
            layout.SetColumnSpan(status, 2);
            layout.Controls.Add(actions, 0, 3);
            layout.SetColumnSpan(actions, 2);
            Controls.Add(layout);

            var about = new ToolStripMenuItem("&About");
            about.DropDownItems.Add("&OpenXR License", null, (sender, e) => ShowOpenXRLicense());
            MainMenuStrip = new MenuStrip();
            MainMenuStrip.Items.Add(about);
            Controls.Add(MainMenuStrip);

            DisableActions();
            folder.TextChanged += (sender, e) => SelectionChanged();
            browse.Click += (sender, e) => Browse();
            patch.Click += async (sender, e) => await ChangeFilesAsync(false);
            revert.Click += async (sender, e) => await ChangeFilesAsync(true);
            debounce.Tick += async (sender, e) =>
            {
                debounce.Stop();
                await VerifyAsync();
            };
            Shown += async (sender, e) => await SelectInitialFolderAsync(selectedFolder);
            FormClosing += (sender, e) => { if (changingFiles) e.Cancel = true; };
        }

        private async Task SelectInitialFolderAsync(string selectedFolder)
        {
            if (string.IsNullOrWhiteSpace(selectedFolder))
            {
                int version = selectionVersion;
                try
                {
                    selectedFolder = await Task.Run(() => GameDiscovery.FindGameFolder());
                }
                catch (Exception)
                {
                    // Manual selection also works when the Steam registry or VDF is inaccessible.
                }
                if (IsDisposed || selectionVersion != version) return;
            }

            if (string.IsNullOrWhiteSpace(selectedFolder))
                status.Text = "Select the game folder to check its files.";
            else
                folder.Text = selectedFolder;
        }

        private void ShowOpenXRLicense()
        {
            using (var dialog = new Form
            {
                Text = "OpenXR License",
                Font = Font,
                AutoScaleMode = AutoScaleMode.Font,
                ClientSize = new Size(740, 480),
                MinimumSize = new Size(360, 240),
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                Padding = new Padding(10)
            })
            {
                var notice = new TextBox
                {
                    AccessibleName = "OpenXR license text",
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = false,
                    ScrollBars = ScrollBars.Both,
                    Dock = DockStyle.Fill,
                    Text = OpenXRLicense.ReadText().Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
                };
                var close = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.Cancel };
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft
                };
                buttons.Controls.Add(close);
                dialog.Controls.Add(notice);
                dialog.Controls.Add(buttons);
                dialog.CancelButton = close;
                dialog.ActiveControl = notice;
                dialog.ShowDialog(this);
            }
        }

        private void Browse()
        {
            using (var picker = new FolderBrowserDialog
            {
                Description = "Select the folder containing Awesome.exe",
                ShowNewFolderButton = false
            })
            {
                if (Directory.Exists(folder.Text)) picker.SelectedPath = folder.Text;
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    if (folder.Text == picker.SelectedPath)
                        SelectionChanged();
                    else
                        folder.Text = picker.SelectedPath;
                }
            }
        }

        private void SelectionChanged()
        {
            selectionVersion++;
            DisableActions();
            debounce.Stop();
            if (string.IsNullOrWhiteSpace(folder.Text))
            {
                status.Text = "Select the game folder to check its files.";
                return;
            }
            status.Text = "Checking files…";
            debounce.Start();
        }

        private async Task VerifyAsync(string operationError = null)
        {
            int version = selectionVersion;
            string path = folder.Text.Trim();
            DisableActions();
            try
            {
                PatchStatus result = await Task.Run(() => service.Inspect(path));
                if (IsDisposed || version != selectionVersion) return;
                status.Text = operationError == null ? result.Message : operationError + " " + result.Message;
                patch.Enabled = result.CanPatch;
                revert.Enabled = result.CanRevert;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && version == selectionVersion)
                    status.Text = (operationError == null ? "" : operationError + " ") +
                        "Could not verify the game folder. " + ex.Message;
            }
        }

        private async Task ChangeFilesAsync(bool restore)
        {
            if (changingFiles) return;
            if (restore && MessageBox.Show(this, "Restore both original executables and the game assembly, and remove the patch plugin, OpenXR loader and SteamVR artwork registration?", "Revert patch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            changingFiles = true;
            int version = ++selectionVersion;
            string path = folder.Text.Trim();
            folder.Enabled = browse.Enabled = false;
            DisableActions();
            status.Text = restore ? "Restoring original files…" : "Applying patch…";
            string error = null;
            try
            {
                await Task.Run(() =>
                {
                    if (restore) service.Revert(path);
                    else service.Patch(path);
                });
            }
            catch (Exception ex)
            {
                error = (restore ? "Revert failed. " : "Patch failed. ") + ex.Message;
            }
            finally
            {
                if (!IsDisposed && version == selectionVersion)
                {
                    status.Text = error ?? "Checking files…";
                    await VerifyAsync(error);
                    folder.Enabled = browse.Enabled = true;
                }
                changingFiles = false;
            }
        }

        private void DisableActions()
        {
            patch.Enabled = revert.Enabled = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) debounce.Dispose();
            base.Dispose(disposing);
        }
    }
}
