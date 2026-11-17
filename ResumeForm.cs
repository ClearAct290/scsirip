using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CdRipper
{
    /// <summary>
    /// Shown at start-up when paused progress exists. Polls every drive in the background and
    /// puts a checkmark next to each drive once the right disc (TOC + PCM hash) is inserted.
    /// </summary>
    internal sealed class ResumeForm : Form
    {
        private sealed class Row
        {
            public PausedDrive Pd;
            public CheckBox Chk;
            public Label Status;
            public VerifyResult Result = VerifyResult.NoDisc;
            public DiscInfo Disc;
        }

        private readonly PausedState state;
        private readonly List<Row> rows = new List<Row>();
        private CheckBox chkAccept;
        private Thread poller;
        private volatile bool stop;
        private bool prompted;

        public ResumeForm(PausedState state)
        {
            this.state = state;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "Resume paused rip";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 130 + Math.Max(1, state.Drives.Count) * 58 + 70);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);

            Label head = new Label();
            head.Location = new Point(14, 12);
            head.Size = new Size(612, 54);
            head.Text = "A paused rip was found (saved " + state.SavedUtc + " UTC).\r\n" +
                        "Put each disc back into the drive shown below. A checkmark appears when the disc matches.";
            Controls.Add(head);

            int y = 76;
            foreach (PausedDrive pd in state.Drives)
            {
                Row r = new Row();
                r.Pd = pd;
                r.Chk = new CheckBox();
                r.Chk.AutoCheck = false;
                r.Chk.AutoSize = true;
                r.Chk.Font = new Font(Font, FontStyle.Bold);
                r.Chk.Location = new Point(16, y);
                r.Chk.Text = pd.Letter + ":  " + pd.DriveName + "   -   insert: \"" + pd.Excerpt + "\"";
                r.Status = new Label();
                r.Status.Location = new Point(38, y + 22);
                r.Status.Size = new Size(580, 18);
                r.Status.Text = "Waiting for disc...";
                Controls.Add(r.Chk);
                Controls.Add(r.Status);
                rows.Add(r);
                y += 58;
            }

            chkAccept = new CheckBox();
            chkAccept.Location = new Point(16, y + 4);
            chkAccept.Size = new Size(608, 36);
            chkAccept.Text = "Accept a disc whose track layout matches even if the PCM hash differs (some drives return slightly shifted audio when re-reading)";
            chkAccept.CheckedChanged += delegate { EvaluateAll(); };
            Controls.Add(chkAccept);

            Button skip = new Button();
            skip.Text = "Not now (keep progress)";
            skip.Location = new Point(16, y + 46);
            skip.Size = new Size(190, 30);
            skip.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(skip);

            Button discard = new Button();
            discard.Text = "Discard paused progress";
            discard.Location = new Point(216, y + 46);
            discard.Size = new Size(190, 30);
            discard.Click += delegate
            {
                if (MessageBox.Show(this, "Discard the saved paused progress? The partially ripped files stay where they are.",
                    "Discard", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    PausedStore.Delete();
                    DialogResult = DialogResult.Abort;
                }
            };
            Controls.Add(discard);

            Shown += delegate
            {
                poller = new Thread(new ThreadStart(PollLoop));
                poller.IsBackground = true;
                poller.Start();
            };
            FormClosing += delegate { stop = true; };
        }

        private static bool IsOk(VerifyResult r, bool accept)
        {
            return r == VerifyResult.Verified || (accept && r == VerifyResult.TocOnly);
        }

        private void PollLoop()
        {
            while (!stop)
            {
                for (int i = 0; i < rows.Count && !stop; i++)
                {
                    Row r = rows[i];
                    if (r.Result == VerifyResult.Verified) continue;
                    DiscInfo d;
                    string status;
                    VerifyResult res;
                    try { res = ResumeCheck.Verify(r.Pd, out d, out status); }
                    catch (Exception ex) { res = VerifyResult.NoDisc; d = null; status = "Error: " + ex.Message; }

                    VerifyResult resF = res;
                    DiscInfo dF = d;
                    string statusF = status;
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            r.Result = resF;
                            r.Disc = dF;
                            r.Status.Text = statusF;
                            r.Chk.Checked = IsOk(resF, chkAccept.Checked);
                            EvaluateAll();
                        }));
                    }
                    catch (InvalidOperationException) { return; }
                }
                for (int w = 0; w < 15 && !stop; w++) Thread.Sleep(100);
            }
        }

        private void EvaluateAll()
        {
            if (prompted) return;
            bool all = rows.Count > 0;
            foreach (Row r in rows)
            {
                bool ok = IsOk(r.Result, chkAccept.Checked);
                r.Chk.Checked = ok;
                if (!ok) all = false;
            }
            if (!all) return;

            prompted = true;
            stop = true;
            DialogResult a = MessageBox.Show(this,
                "All discs are in place.\r\n\r\nAre you ready to resume?",
                "Resume ripping", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (a == DialogResult.Yes)
            {
                DialogResult = DialogResult.Yes;
            }
            else
            {
                MessageBox.Show(this,
                    "Okay. Restart the program to unpause again whenever you are ready.\r\n\r\n" +
                    "Note: if you pause another rip before then, the new paused progress will overwrite the progress saved now.",
                    "Resume later", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.No;
            }
        }

        public bool EjectWhenDone { get { return state.EjectWhenDone; } }

        public List<DriveJob> BuildJobs(RipSession session)
        {
            List<DriveJob> jobs = new List<DriveJob>();
            foreach (Row r in rows)
            {
                DriveJob j = new DriveJob(session, r.Disc, r.Pd.Folder);
                j.Resuming = true;
                j.ResumeTrackNumber = r.Pd.TrackNumber;
                j.ResumeSector = r.Pd.SectorInTrack;
                jobs.Add(j);
            }
            return jobs;
        }
    }
}
