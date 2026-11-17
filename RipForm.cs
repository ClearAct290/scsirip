using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace CdRipper
{
    internal enum RipOutcome { Completed, CompletedWithErrors, Aborted, Paused }

    /// <summary>Progress screen: total bar, per-drive bars (only if >1 drive), console, Pause/Abort.</summary>
    internal sealed class RipForm : Form
    {
        private readonly RipSession session;
        private readonly List<DriveJob> jobs;
        private readonly bool resumed;
        private readonly bool ejectAfter;

        private ProgressBar totalBar;
        private Label lblTotal;
        private Button btnPause;
        private Button btnAbort;
        private TextBox console;
        private readonly List<Label> driveLabels = new List<Label>();
        private readonly List<ProgressBar> driveBars = new List<ProgressBar>();
        private System.Windows.Forms.Timer timer;
        private readonly StringBuilder logAll = new StringBuilder();
        private readonly System.Diagnostics.Stopwatch wall = new System.Diagnostics.Stopwatch();
        private bool finished;

        public RipOutcome Outcome = RipOutcome.Completed;

        public RipForm(RipSession session, List<DriveJob> jobs, bool resumed, bool eject)
        {
            this.session = session;
            this.jobs = jobs;
            this.resumed = resumed;
            this.ejectAfter = eject;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = resumed ? "Ripping (resumed)" : "Ripping";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1040, 560);
            MinimumSize = new Size(860, 420);
            Font = new Font("Segoe UI", 9F);

            console = new TextBox();
            console.Multiline = true;
            console.ReadOnly = true;
            console.ScrollBars = ScrollBars.Vertical;
            console.Dock = DockStyle.Fill;
            console.BackColor = Color.Black;
            console.ForeColor = Color.Gainsboro;
            console.Font = new Font("Consolas", 9F);

            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 440;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 84;
            lblTotal = new Label();
            lblTotal.Location = new Point(12, 10);
            lblTotal.Size = new Size(416, 32);
            lblTotal.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            totalBar = new ProgressBar();
            totalBar.Location = new Point(12, 46);
            totalBar.Size = new Size(416, 28);
            totalBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            totalBar.Maximum = 10000;
            top.Controls.Add(lblTotal);
            top.Controls.Add(totalBar);

            Panel rows = new Panel();
            rows.Dock = DockStyle.Fill;
            rows.AutoScroll = true;
            bool multi = jobs.Count > 1;
            int y = 8;
            foreach (DriveJob j in jobs)
            {
                Label l = new Label();
                l.Location = new Point(12, y);
                l.Size = new Size(400, multi ? 18 : 40);
                l.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                rows.Controls.Add(l);
                driveLabels.Add(l);
                if (multi)
                {
                    ProgressBar b = new ProgressBar();
                    b.Location = new Point(12, y + 20);
                    b.Size = new Size(400, 14);
                    b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                    b.Maximum = 10000;
                    rows.Controls.Add(b);
                    driveBars.Add(b);
                    y += 46;
                }
                else y += 44;
            }

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 56;
            btnPause = new Button();
            btnPause.Text = "Pause (save progress)";
            btnPause.Location = new Point(12, 12);
            btnPause.Size = new Size(200, 32);
            btnPause.Click += OnPauseClick;
            btnAbort = new Button();
            btnAbort.Text = "Abort";
            btnAbort.Location = new Point(224, 12);
            btnAbort.Size = new Size(200, 32);
            btnAbort.Click += OnAbortClick;
            buttons.Controls.Add(btnPause);
            buttons.Controls.Add(btnAbort);

            left.Controls.Add(rows);
            left.Controls.Add(buttons);
            left.Controls.Add(top);

            Controls.Add(console);
            Controls.Add(left);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += OnTick;

            Shown += OnShown;
            FormClosing += OnFormClosing;
        }

        private void OnShown(object sender, EventArgs e)
        {
            wall.Start();
            foreach (DriveJob j in jobs) j.Start();
            timer.Start();
        }

        private void OnPauseClick(object sender, EventArgs e)
        {
            session.PauseRequested = true;
            btnPause.Enabled = false;
            session.Log("Pause requested - stopping at the next safe block and saving each drive's sector position...");
        }

        private void OnAbortClick(object sender, EventArgs e)
        {
            if (finished) { DialogResult = DialogResult.OK; return; }      // button acts as "Close"
            DialogResult r = MessageBox.Show(this,
                "Are you sure you want to abort?\r\n\r\nThe rip stops completely and every file is left in exactly the state it is in right now.",
                "Abort rip", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes || finished) return;
            session.AbortRequested = true;
            btnAbort.Enabled = false;
            btnPause.Enabled = false;
            session.Log("Abort requested - stopping...");
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!finished)
            {
                e.Cancel = true;
                OnAbortClick(this, EventArgs.Empty);
            }
        }

        private void FlushLog()
        {
            string[] lines = session.Drain();
            if (lines.Length == 0) return;
            StringBuilder sb = new StringBuilder();
            foreach (string s in lines) sb.Append(s).Append("\r\n");
            logAll.Append(sb.ToString());
            if (console.TextLength > 300000) console.Text = console.Text.Substring(console.TextLength - 200000);
            console.AppendText(sb.ToString());
        }

        private void OnTick(object sender, EventArgs e)
        {
            FlushLog();
            long done = 0, total = 0;
            double maxEta = -1;
            bool anyRunning = false;
            double speedSum = 0;

            for (int i = 0; i < jobs.Count; i++)
            {
                DriveJob j = jobs[i];
                int d = j.SectorsDone;
                done += d;
                total += j.TotalSectors;
                bool running = j.State == JobState.Running || j.State == JobState.Pending;
                if (running)
                {
                    anyRunning = true;
                    speedSum += j.SpeedX10 / 10.0;
                    double eta = j.EtaSeconds();
                    if (eta > maxEta) maxEta = eta;
                }
                double pct = j.TotalSectors > 0 ? 100.0 * d / j.TotalSectors : 0;
                string txt;
                switch (j.State)
                {
                    case JobState.Done: txt = "Done"; break;
                    case JobState.Paused: txt = "Paused at track " + j.PausedTrackNumber; break;
                    case JobState.Aborted: txt = "Aborted"; break;
                    case JobState.Failed: txt = "FAILED: " + j.Message; break;
                    default:
                        txt = string.Format("Track {0:00} ({1}/{2})   {3:0.0}x   {4:0.0}%",
                            j.CurrentTrackNumber, j.CurrentTrackOrdinal, j.Disc.Tracks.Count, j.SpeedX10 / 10.0, pct);
                        break;
                }
                driveLabels[i].Text = j.Disc.Letter + ":  " + j.Disc.DriveName + "\r\n" + txt;
                if (jobs.Count > 1)
                {
                    driveLabels[i].Text = j.Disc.Letter + ":  " + j.Disc.DriveName + " - " + txt;
                    driveBars[i].Value = Math.Min(driveBars[i].Maximum, (int)(pct * 100));
                }
            }

            double tpct = total > 0 ? 100.0 * done / total : 0;
            totalBar.Value = Math.Min(totalBar.Maximum, (int)(tpct * 100));
            lblTotal.Text = string.Format("Total progress {0:0.0}%   |   elapsed {1}   |   ETA {2}   |   {3:0.0}x{4}",
                tpct, Util.Time(wall.Elapsed.TotalSeconds), anyRunning ? Util.Time(maxEta) : "--:--", speedSum,
                jobs.Count > 1 ? " combined" : "");

            if (!anyRunning && !finished) Finish();
        }

        private void Finish()
        {
            finished = true;
            timer.Stop();
            wall.Stop();

            bool anyPaused = false, anyFailed = false;
            foreach (DriveJob j in jobs)
            {
                if (j.State == JobState.Paused) anyPaused = true;
                if (j.State == JobState.Failed) anyFailed = true;
            }

            if (session.AbortRequested) Outcome = RipOutcome.Aborted;
            else if (anyPaused) { Outcome = RipOutcome.Paused; SavePausedState(); }
            else Outcome = anyFailed ? RipOutcome.CompletedWithErrors : RipOutcome.Completed;

            if (Outcome == RipOutcome.Completed) session.Log("==== ALL DRIVES FINISHED ====");
            else if (Outcome == RipOutcome.CompletedWithErrors) session.Log("==== FINISHED - one or more drives reported errors (see above) ====");
            FlushLog();
            try { File.WriteAllText(AppData.LogFile, logAll.ToString()); } catch { }

            if (Outcome == RipOutcome.Completed || Outcome == RipOutcome.CompletedWithErrors)
            {
                totalBar.Value = Outcome == RipOutcome.Completed ? totalBar.Maximum : totalBar.Value;
                btnPause.Visible = false;
                btnAbort.Text = "Close";
                btnAbort.Enabled = true;
                SystemSounds.Asterisk.Play();
            }
            else DialogResult = DialogResult.OK;
        }

        private void SavePausedState()
        {
            PausedState ps = new PausedState();
            ps.SavedUtc = DateTime.UtcNow.ToString("u");
            ps.EjectWhenDone = ejectAfter;
            foreach (DriveJob j in jobs)
            {
                if (j.State != JobState.Paused) continue;
                PausedDrive pd = new PausedDrive();
                pd.Letter = j.Disc.Letter.ToString();
                pd.DriveName = j.Disc.DriveName;
                pd.Excerpt = j.Disc.Excerpt(40);
                pd.Folder = j.Folder;
                pd.TocHash = j.Disc.TocHash;
                pd.TrackNumber = j.PausedTrackNumber;
                pd.SectorInTrack = j.PausedSector;
                pd.PcmHash = j.PausedHash;
                ps.Drives.Add(pd);
            }
            try
            {
                PausedStore.Save(ps);
                session.Log("Paused progress for " + ps.Drives.Count + " drive(s) saved to " + AppData.PausedFile);
            }
            catch (Exception ex)
            {
                session.Log("COULD NOT SAVE PAUSED PROGRESS: " + ex.Message);
                MessageBox.Show(this, "Could not save the paused progress:\r\n" + ex.Message, "Pause", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
