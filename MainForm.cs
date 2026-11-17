using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CdRipper
{
    /// <summary>Home screen: drives with a valid audio CD, tick boxes, folders, Rip button.</summary>
    internal sealed class MainForm : Form
    {
        private sealed class DriveRow
        {
            public DiscInfo Disc;
            public Panel Pane;
            public CheckBox Chk;
            public TextBox Txt;
            public Button Btn;
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private readonly List<DriveRow> rows = new List<DriveRow>();
        private readonly Dictionary<char, bool> checks = new Dictionary<char, bool>();
        private readonly Dictionary<char, string> folders = new Dictionary<char, string>();

        private Panel listPanel;
        private Label lblEmpty;
        private Button btnRip;
        private Button btnRefresh;
        private CheckBox chkEject;
        private ToolStripStatusLabel lblStatus;
        private System.Windows.Forms.Timer rescanTimer;

        private bool scanning;
        private bool rescanPending;
        private bool ripping;

        public MainForm()
        {
            BuildUi();
            ApplyIcon();
            Shown += OnShown;
        }

        /// <summary>
        /// Sets the window/taskbar icon from icon.ico next to the exe. The exe's own icon
        /// (shown in Explorer, shortcuts, and the taskbar before the window exists) is baked
        /// in separately at compile time (/win32icon in build.bat, &lt;ApplicationIcon&gt; in
        /// the .csproj) - this only covers the running window, which does not automatically
        /// pick up the exe's embedded icon on all OS/theme combinations.
        /// </summary>
        private void ApplyIcon()
        {
            try
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath) ?? "",
                    "icon.ico");
                if (File.Exists(path)) Icon = new Icon(path);
            }
            catch { /* missing/corrupt icon file should never stop the app from starting */ }
        }

        // ------------------------------------------------------------------ UI
        private void BuildUi()
        {
            Text = "CD Ripper - direct PCM extraction (SCSI pass-through)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 560);
            MinimumSize = new Size(720, 380);
            Font = new Font("Segoe UI", 9F);

            Label header = new Label();
            header.Dock = DockStyle.Top;
            header.Height = 56;
            header.Padding = new Padding(12, 10, 12, 0);
            header.Text = "Insert audio CDs. Every drive that holds a valid CD-DA disc is listed below. " +
                          "Tick the drives you want to rip and choose a destination folder for each one, then press Rip.";

            listPanel = new Panel();
            listPanel.Dock = DockStyle.Fill;
            listPanel.AutoScroll = true;
            listPanel.BackColor = Color.White;

            lblEmpty = new Label();
            lblEmpty.AutoSize = false;
            lblEmpty.Dock = DockStyle.Top;
            lblEmpty.Height = 80;
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.ForeColor = Color.DimGray;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 56;

            btnRefresh = new Button();
            btnRefresh.Text = "Refresh drives";
            btnRefresh.Location = new Point(12, 12);
            btnRefresh.Size = new Size(120, 32);
            btnRefresh.Click += delegate { StartScan(); };

            chkEject = new CheckBox();
            chkEject.Text = "Eject discs when finished";
            chkEject.AutoSize = true;
            chkEject.Location = new Point(150, 19);
            chkEject.Checked = Settings.Get("eject", "0") == "1";
            chkEject.CheckedChanged += delegate { Settings.Set("eject", chkEject.Checked ? "1" : "0"); };

            btnRip = new Button();
            btnRip.Text = "Rip selected drives";
            btnRip.Size = new Size(170, 32);
            btnRip.Location = new Point(bottom.Width - 182, 12);
            btnRip.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRip.Click += delegate { OnRipClick(); };

            bottom.Controls.Add(btnRefresh);
            bottom.Controls.Add(chkEject);
            bottom.Controls.Add(btnRip);

            StatusStrip status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("Ready");
            status.Items.Add(lblStatus);

            listPanel.Controls.Add(lblEmpty);
            Controls.Add(listPanel);
            Controls.Add(bottom);
            Controls.Add(header);
            Controls.Add(status);
            AcceptButton = btnRip;

            rescanTimer = new System.Windows.Forms.Timer();
            rescanTimer.Interval = 1500;
            rescanTimer.Tick += delegate { rescanTimer.Stop(); StartScan(); };
        }

        private void OnShown(object sender, EventArgs e)
        {
            CheckPausedProgress();
            StartScan();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && !ripping)
            {
                int w = (int)m.WParam.ToInt64();
                if (w == DBT_DEVICEARRIVAL || w == DBT_DEVICEREMOVECOMPLETE)
                {
                    rescanTimer.Stop();
                    rescanTimer.Start();      // debounce: scan 1.5 s after the last event
                }
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ scanning
        private void StartScan()
        {
            if (ripping) return;
            if (scanning) { rescanPending = true; return; }
            scanning = true;
            lblStatus.Text = "Scanning CD/DVD drives...";
            Thread th = new Thread(new ThreadStart(ScanWorker));
            th.IsBackground = true;
            th.Start();
        }

        private void ScanWorker()
        {
            List<DiscInfo> found = new List<DiscInfo>();
            List<string> notes = new List<string>();
            int drives = 0;
            try
            {
                foreach (System.IO.DriveInfo di in System.IO.DriveInfo.GetDrives())
                {
                    if (di.DriveType != System.IO.DriveType.CDRom) continue;
                    drives++;
                    char letter = char.ToUpper(di.Name[0]);
                    string err;
                    ScsiDevice dev = ScsiDevice.Open(letter, out err);
                    if (dev == null) { notes.Add(letter + ": " + err); continue; }
                    try
                    {
                        string name = dev.Inquiry();
                        if (string.IsNullOrEmpty(name)) name = "CD/DVD drive";
                        if (!DiscReader.WaitReady(dev, 3000)) continue;
                        DiscInfo d = DiscReader.Read(dev, letter, name);
                        if (d != null && d.Tracks.Count > 0) found.Add(d);
                    }
                    finally { dev.Dispose(); }
                }
            }
            catch (Exception ex) { notes.Add(ex.Message); }

            int driveCount = drives;
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(new MethodInvoker(delegate { ApplyScan(found, notes, driveCount); }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyScan(List<DiscInfo> found, List<string> notes, int driveCount)
        {
            scanning = false;
            foreach (DriveRow r in rows)
            {
                checks[r.Disc.Letter] = r.Chk.Checked;
                folders[r.Disc.Letter] = r.Txt.Text;
            }

            listPanel.SuspendLayout();
            Control[] old = new Control[listPanel.Controls.Count];
            listPanel.Controls.CopyTo(old, 0);
            listPanel.Controls.Clear();
            foreach (Control c in old) if (c != lblEmpty) c.Dispose();
            rows.Clear();

            int y = 8;
            foreach (DiscInfo d in found)
            {
                DriveRow r = CreateRow(d);
                r.Pane.Location = new Point(8, y);
                listPanel.Controls.Add(r.Pane);
                rows.Add(r);
                y += r.Pane.Height + 8;
            }

            if (found.Count == 0)
            {
                lblEmpty.Text = driveCount == 0
                    ? "No CD/DVD drives were found."
                    : "No audio CD detected in any drive.\r\nInsert an audio CD and press \"Refresh drives\".";
                listPanel.Controls.Add(lblEmpty);
            }
            listPanel.ResumeLayout();

            btnRip.Enabled = found.Count > 0;
            string st = found.Count + " drive(s) with audio CD";
            if (notes.Count > 0)
                st += "   |   could not access: " + string.Join("; ", notes.ToArray()) + "  (try Run as administrator)";
            lblStatus.Text = st;

            if (rescanPending) { rescanPending = false; StartScan(); }
        }

        private DriveRow CreateRow(DiscInfo d)
        {
            DriveRow r = new DriveRow();
            r.Disc = d;

            Panel p = new Panel();
            p.Height = 88;
            p.Width = Math.Max(400, listPanel.ClientSize.Width - 28);
            p.BorderStyle = BorderStyle.FixedSingle;
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            CheckBox chk = new CheckBox();
            chk.AutoSize = true;
            chk.Font = new Font(Font, FontStyle.Bold);
            chk.Location = new Point(10, 8);
            chk.Text = d.Letter + ":   " + d.DriveName;
            bool c;
            chk.Checked = checks.TryGetValue(d.Letter, out c) ? c : Settings.Get("check." + d.Letter, "1") == "1";

            Label info = new Label();
            info.AutoSize = false;
            info.Location = new Point(30, 30);
            info.Size = new Size(p.Width - 44, 18);
            info.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            info.Text = string.Format("{0} audio track(s)  |  {1}  |  {2}", d.Tracks.Count,
                Util.Time(d.TotalSectors / 75.0),
                d.HasCdText ? "CD-TEXT: " + d.Excerpt(70) : "no CD-TEXT (files will be named Track NN.wav)");

            string def;
            if (!folders.TryGetValue(d.Letter, out def))
            {
                string my = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (string.IsNullOrEmpty(my)) my = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                def = Settings.Get("folder." + d.Letter, my);
            }
            TextBox txt = new TextBox();
            txt.ReadOnly = true;
            txt.Location = new Point(30, 54);
            txt.Size = new Size(p.Width - 168, 24);
            txt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txt.Text = def;

            Button btn = new Button();
            btn.Text = "Set Folder...";
            btn.Size = new Size(104, 26);
            btn.Location = new Point(p.Width - 124, 52);
            btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            chk.CheckedChanged += delegate
            {
                btn.Enabled = chk.Checked;          // greyed out when the drive is unticked
                txt.Enabled = chk.Checked;
                Settings.Set("check." + d.Letter, chk.Checked ? "1" : "0");
            };
            btn.Enabled = chk.Checked;
            txt.Enabled = chk.Checked;
            btn.Click += delegate { ChooseFolder(d.Letter, txt); };

            p.Controls.Add(chk);
            p.Controls.Add(info);
            p.Controls.Add(txt);
            p.Controls.Add(btn);

            r.Pane = p; r.Chk = chk; r.Txt = txt; r.Btn = btn;
            return r;
        }

        private void ChooseFolder(char letter, TextBox txt)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.Description = "Choose the folder that the tracks from drive " + letter + ": will be ripped to.";
            fbd.ShowNewFolderButton = true;
            if (Directory.Exists(txt.Text)) fbd.SelectedPath = txt.Text;
            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                txt.Text = fbd.SelectedPath;
                folders[letter] = fbd.SelectedPath;
                Settings.Set("folder." + letter, fbd.SelectedPath);
            }
        }

        // ------------------------------------------------------------------ ripping
        private void OnRipClick()
        {
            List<DriveRow> sel = rows.FindAll(delegate(DriveRow r) { return r.Chk.Checked; });
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one drive to rip.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RipSession session = new RipSession();
            List<DriveJob> jobs = new List<DriveJob>();
            Dictionary<string, char> used = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
            foreach (DriveRow r in sel)
            {
                string folder = r.Txt.Text.Trim();
                if (folder.Length == 0)
                {
                    MessageBox.Show(this, "Choose a folder for drive " + r.Disc.Letter + ": first.", "Folder missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try { folder = Path.GetFullPath(folder).TrimEnd('\\'); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The folder for drive " + r.Disc.Letter + ": is not valid:\r\n" + ex.Message, "Folder invalid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                char other;
                if (used.TryGetValue(folder, out other))
                {
                    MessageBox.Show(this, "Drives " + other + ": and " + r.Disc.Letter + ": use the same folder.\r\nTracks with the same name would overwrite each other - give each drive its own folder.",
                        "Same folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                used[folder] = r.Disc.Letter;
                jobs.Add(new DriveJob(session, r.Disc, folder));
            }

            // free space
            Dictionary<string, long> need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (DriveJob j in jobs)
            {
                string root = Path.GetPathRoot(j.Folder);
                long v;
                need.TryGetValue(root, out v);
                need[root] = v + j.EstimatedBytes();
            }
            foreach (KeyValuePair<string, long> kv in need)
            {
                try
                {
                    DriveInfo di = new DriveInfo(kv.Key);
                    if (di.IsReady && di.AvailableFreeSpace < kv.Value)
                    {
                        string m = string.Format("{0} has about {1:N0} MiB free but the rip needs about {2:N0} MiB.\r\n\r\nContinue anyway?",
                            kv.Key, di.AvailableFreeSpace / 1048576, kv.Value / 1048576);
                        if (MessageBox.Show(this, m, "Low disk space", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                            return;
                    }
                }
                catch { }
            }

            // existing files that would be overwritten
            List<string> existing = new List<string>();
            foreach (DriveJob j in jobs)
                foreach (TrackInfo t in j.Disc.Tracks)
                {
                    string p = j.PathFor(t);
                    if (File.Exists(p)) existing.Add(p);
                }
            if (existing.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(existing.Count + " WAV file(s) in the selected folder(s) already exist and would be overwritten:\r\n\r\n");
                for (int i = 0; i < existing.Count && i < 8; i++) sb.Append("   " + existing[i] + "\r\n");
                if (existing.Count > 8) sb.Append("   ... and " + (existing.Count - 8) + " more\r\n");
                sb.Append("\r\nDo you really want to overwrite them?");
                if (MessageBox.Show(this, sb.ToString(), "Overwrite files?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }

            RunRip(session, jobs, false, chkEject.Checked);
        }

        private void CheckPausedProgress()
        {
            PausedState ps = PausedStore.Load();
            if (ps == null || ps.Drives.Count == 0) return;
            ResumeForm rf = new ResumeForm(ps);
            DialogResult r = rf.ShowDialog(this);
            if (r == DialogResult.Yes)
            {
                RipSession session = new RipSession();
                List<DriveJob> jobs = rf.BuildJobs(session);
                bool eject = rf.EjectWhenDone;
                rf.Dispose();
                RunRip(session, jobs, true, eject);
            }
            else rf.Dispose();
        }

        private void RunRip(RipSession session, List<DriveJob> jobs, bool resumed, bool eject)
        {
            ripping = true;
            RipForm rf = new RipForm(session, jobs, resumed, eject);
            rf.ShowDialog(this);
            RipOutcome outcome = rf.Outcome;
            rf.Dispose();
            ripping = false;

            if (resumed && outcome != RipOutcome.Paused) PausedStore.Delete();

            switch (outcome)
            {
                case RipOutcome.Aborted:
                    MessageBox.Show(this, "The rip was aborted.\r\nAll files were left exactly as they were when it stopped.", "Aborted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case RipOutcome.Paused:
                    MessageBox.Show(this, "The rip is paused and its position was saved.\r\n\r\nRestart the program whenever you want to resume it. " +
                        "(Pausing another rip first will overwrite this saved progress.)", "Paused", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case RipOutcome.Completed:
                case RipOutcome.CompletedWithErrors:
                    if (eject) foreach (DriveJob j in jobs) if (j.State == JobState.Done) ScsiDevice.TryEject(j.Disc.Letter);
                    break;
            }
            StartScan();
        }
    }
}
