using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CdRipper
{
    internal enum JobState { Pending, Running, Paused, Done, Aborted, Failed }

    /// <summary>Shared by all drive threads of one rip: pause/abort flags and a thread-safe console log.</summary>
    internal sealed class RipSession
    {
        public volatile bool PauseRequested;
        public volatile bool AbortRequested;
        private readonly Queue<string> queue = new Queue<string>();

        public void Log(string line)
        {
            lock (queue) { queue.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + line); }
        }

        public string[] Drain()
        {
            lock (queue)
            {
                string[] a = queue.ToArray();
                queue.Clear();
                return a;
            }
        }
    }

    /// <summary>One drive = one thread = one disc being ripped track by track.</summary>
    internal sealed class DriveJob
    {
        public const int ChunkSectors = 26;           // 26 x 2352 = 61,152 bytes per READ CD
        private const int MaxUnreadable = 3000;

        private readonly RipSession session;
        private readonly string tag;
        public readonly DiscInfo Disc;
        public readonly string Folder;
        public readonly int TotalSectors;

        public bool Resuming;
        public int ResumeTrackNumber;
        public int ResumeSector;

        public volatile JobState State = JobState.Pending;
        public volatile int SectorsDone;
        public volatile int StartSectors;
        public volatile int CurrentTrackNumber;
        public volatile int CurrentTrackOrdinal;
        public volatile int SpeedX10;                 // current speed x 10 (e.g. 245 = 24.5x)
        public volatile string Message = "";

        public int PausedTrackNumber;
        public int PausedSector;
        public string PausedHash = "";
        public int Unreadable;

        public readonly Stopwatch Clock = new Stopwatch();
        private double lastProgressLog;
        private Thread thread;

        public DriveJob(RipSession session, DiscInfo disc, string folder)
        {
            this.session = session;
            Disc = disc;
            Folder = folder;
            TotalSectors = disc.TotalSectors;
            tag = "[" + disc.Letter + ":] ";
        }

        public string PathFor(TrackInfo t) { return Path.Combine(Folder, Util.TrackFileName(t)); }

        public long EstimatedBytes()
        {
            return (long)TotalSectors * ScsiDevice.SectorSize + Disc.Tracks.Count * 4096L;
        }

        public double EtaSeconds()
        {
            double el = Clock.Elapsed.TotalSeconds;
            int ripped = SectorsDone - StartSectors;
            if (el < 2 || ripped <= 0) return -1;
            return (TotalSectors - SectorsDone) / (ripped / el);
        }

        public void Start()
        {
            State = JobState.Running;
            thread = new Thread(new ThreadStart(Run));
            thread.IsBackground = true;
            thread.Name = "Rip " + Disc.Letter;
            thread.Start();
        }

        private void Fail(string msg)
        {
            Message = msg;
            session.Log(tag + "ERROR: " + msg);
            State = JobState.Failed;
        }

        private void Run()
        {
            ScsiDevice dev = null;
            try
            {
                string err;
                dev = ScsiDevice.Open(Disc.Letter, out err);
                if (dev == null) { Fail("cannot open drive: " + err); return; }
                if (!DiscReader.WaitReady(dev, 8000)) { Fail("drive not ready / no disc inserted"); return; }

                Clock.Start();
                LogSummary(dev);
                Directory.CreateDirectory(Folder);

                int startIdx = 0;
                if (Resuming)
                {
                    startIdx = Disc.Tracks.FindIndex(delegate(TrackInfo x) { return x.Number == ResumeTrackNumber; });
                    if (startIdx < 0) { Fail("paused track " + ResumeTrackNumber + " is not on this disc"); return; }
                }
                int doneBefore = 0;
                for (int i = 0; i < startIdx; i++) doneBefore += Disc.Tracks[i].Sectors;
                SectorsDone = doneBefore + (Resuming ? ResumeSector : 0);
                StartSectors = SectorsDone;

                for (int i = startIdx; i < Disc.Tracks.Count; i++)
                {
                    JobState r = RipTrack(dev, i, doneBefore, (i == startIdx && Resuming) ? ResumeSector : 0);
                    if (r == JobState.Aborted)
                    {
                        session.Log(tag + "Aborted. Files were left exactly as they were when the rip stopped.");
                        State = JobState.Aborted;
                        return;
                    }
                    if (r == JobState.Paused)
                    {
                        session.Log(string.Format("{0}PAUSED at track {1:00}, sector {2:N0} (hash {3}).", tag,
                            PausedTrackNumber, PausedSector, PausedHash.Length > 0 ? PausedHash.Substring(0, 12) + "..." : "n/a"));
                        State = JobState.Paused;
                        return;
                    }
                    doneBefore += Disc.Tracks[i].Sectors;
                }

                double secs = Clock.Elapsed.TotalSeconds;
                int ripped = SectorsDone - StartSectors;
                session.Log(string.Format("{0}FINISHED in {1}. Average speed {2:0.0}x. Unreadable sectors replaced by silence: {3}.",
                    tag, Util.Time(secs), secs > 0 ? ripped / secs / 75.0 : 0, Unreadable));
                State = JobState.Done;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                Clock.Stop();
                if (dev != null) dev.Dispose();
            }
        }

        private void LogSummary(ScsiDevice dev)
        {
            long audioBytes = (long)TotalSectors * ScsiDevice.SectorSize;
            session.Log(tag + "==== Drive " + Disc.Letter + ": " + Disc.DriveName + " ====");
            session.Log(string.Format("{0}Disc: {1} audio track(s), {2:N0} sectors, running time {3} (MM:SS.FF)",
                tag, Disc.Tracks.Count, TotalSectors, Util.Msf(TotalSectors)));
            session.Log(tag + "Exact PCM size: " + Util.Size(audioBytes) + " = " + TotalSectors + " sectors x 2352 bytes");
            session.Log(tag + "Format: CD-DA raw PCM, signed 16-bit little-endian, 44,100 Hz, stereo; 2352 bytes/sector, 75 sectors/s = 176,400 B/s at 1x");
            session.Log(tag + "Output: WAV (RIFF/PCM) in " + Folder);
            session.Log(tag + (Disc.HasCdText
                ? "CD-TEXT: found (" + Disc.Excerpt(60) + ") - track titles are written as ID3v2 tags"
                : "CD-TEXT: none found - files are named 'Track NN.wav' and carry no ID3 tag"));
            session.Log(tag + "TOC hash (SHA-1): " + Disc.TocHash);

            bool ok = dev.SetMaxSpeed();
            session.Log(tag + (ok ? "SET CD SPEED: maximum (0xFFFF) accepted"
                                  : "SET CD SPEED rejected (" + dev.ErrorText() + ") - drive default speed will be used"));
            int kb = dev.GetMaxReadSpeedKBps();
            if (kb > 0)
            {
                double x = kb / 176.4;
                double est = (TotalSectors / 75.0) / x;
                session.Log(string.Format("{0}Drive-reported max read speed: {1:N0} kB/s (~{2:0.0}x). Estimated total time at that speed: {3} (real speed varies across the disc)",
                    tag, kb, x, Util.Time(est)));
            }
            else
                session.Log(tag + "Drive does not report its max read speed - speed and ETA are measured live");

            foreach (TrackInfo t in Disc.Tracks)
                session.Log(string.Format("{0}  track {1:00}: LBA {2,7:N0}  {3,7:N0} sectors  {4}  {5}",
                    tag, t.Number, t.StartLba, t.Sectors, Util.Msf(t.Sectors), t.Title));

            if (Resuming)
                session.Log(string.Format("{0}RESUMING at track {1:00}, sector {2:N0}", tag, ResumeTrackNumber, ResumeSector));
        }

        private JobState RipTrack(ScsiDevice dev, int index, int doneBefore, int startSector)
        {
            TrackInfo t = Disc.Tracks[index];
            CurrentTrackNumber = t.Number;
            CurrentTrackOrdinal = index + 1;
            string path = PathFor(t);

            string artist = t.Performer.Length > 0 ? t.Performer : Disc.AlbumPerformer;
            byte[] id3Chunk = new byte[0];
            if (t.Title.Length > 0)
                id3Chunk = WavFile.BuildId3Chunk(WavFile.BuildId3(t.Title, artist, Disc.AlbumTitle, t.Number, Disc.Tracks.Count));

            long dataLen = (long)t.Sectors * ScsiDevice.SectorSize;
            byte[] header = WavFile.BuildHeader(dataLen, id3Chunk.Length);

            session.Log(string.Format("{0}Track {1:00}: LBA {2:N0}..{3:N0}, {4:N0} bytes PCM -> {5}{6}",
                tag, t.Number, t.StartLba, t.StartLba + t.Sectors - 1, dataLen, Path.GetFileName(path),
                id3Chunk.Length > 0 ? "  [ID3v2: \"" + t.Title + "\"]" : ""));

            int sector = startSector;
            FileStream fs;
            if (startSector > 0 && File.Exists(path))
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                long have = (fs.Length - WavFile.HeaderSize) / ScsiDevice.SectorSize;
                if (have < 0) have = 0;
                if (sector > have)
                {
                    session.Log(tag + "File is shorter than the saved position; continuing from sector " + have);
                    sector = (int)have;
                }
            }
            else
            {
                if (startSector > 0) session.Log(tag + "Partial file is missing; restarting this track from sector 0");
                sector = 0;
                fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            }

            using (fs)
            {
                fs.Write(header, 0, header.Length);
                fs.Position = WavFile.HeaderSize + (long)sector * ScsiDevice.SectorSize;
                fs.SetLength(fs.Position);
                SectorsDone = doneBefore + sector;

                byte[] buf = new byte[ChunkSectors * ScsiDevice.SectorSize];
                Stopwatch win = Stopwatch.StartNew();
                int winStart = sector;

                while (sector < t.Sectors)
                {
                    if (session.AbortRequested) return JobState.Aborted;
                    if (session.PauseRequested)
                    {
                        PausedTrackNumber = t.Number;
                        PausedSector = sector;
                        PausedHash = ResumeCheck.ComputePcmHash(dev, t, sector);
                        return JobState.Paused;
                    }

                    int n = Math.Min(ChunkSectors, t.Sectors - sector);
                    ReadChunk(dev, t.StartLba + sector, n, buf);
                    fs.Write(buf, 0, n * ScsiDevice.SectorSize);
                    sector += n;
                    SectorsDone = doneBefore + sector;

                    double w = win.Elapsed.TotalSeconds;
                    if (w >= 1.0)
                    {
                        SpeedX10 = (int)((sector - winStart) / w / 75.0 * 10.0);
                        win.Reset(); win.Start();
                        winStart = sector;
                    }

                    double now = Clock.Elapsed.TotalSeconds;
                    if (now - lastProgressLog >= 5.0)
                    {
                        lastProgressLog = now;
                        double x = SpeedX10 / 10.0;
                        session.Log(string.Format("{0}Track {1:00} ({2}/{3})  {4:0.0}%  sector {5:N0}/{6:N0}  {7:0.0}x ({8:0.00} MB/s)  disc {9:0.0}%  elapsed {10}  ETA {11}",
                            tag, t.Number, CurrentTrackOrdinal, Disc.Tracks.Count, 100.0 * sector / t.Sectors,
                            sector, t.Sectors, x, x * 0.1764, 100.0 * SectorsDone / TotalSectors,
                            Util.Time(now), Util.Time(EtaSeconds())));
                    }
                }

                if (id3Chunk.Length > 0) fs.Write(id3Chunk, 0, id3Chunk.Length);
                fs.Flush();
            }

            session.Log(string.Format("{0}Track {1:00} complete: {2:N0} bytes written", tag, t.Number,
                WavFile.HeaderSize + dataLen + id3Chunk.Length));
            return JobState.Running;      // Running = "finished normally"
        }

        /// <summary>One READ CD block with retries, then sector-by-sector; unreadable sectors become silence.</summary>
        private void ReadChunk(ScsiDevice dev, int lba, int n, byte[] buf)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (dev.ReadCdda(lba, n, buf, 0)) return;
                if (dev.MediaGone) throw new IOException("disc removed or changed (" + dev.ErrorText() + ")");
                session.Log(string.Format("{0}read error at LBA {1:N0} (+{2}): {3} - retry {4}/3",
                    tag, lba, n, dev.ErrorText(), attempt + 1));
                if (session.AbortRequested) return;
            }
            for (int i = 0; i < n; i++)
            {
                bool ok = false;
                for (int a = 0; a < 3 && !ok; a++) ok = dev.ReadCdda(lba + i, 1, buf, i * ScsiDevice.SectorSize);
                if (dev.MediaGone) throw new IOException("disc removed or changed (" + dev.ErrorText() + ")");
                if (!ok)
                {
                    Array.Clear(buf, i * ScsiDevice.SectorSize, ScsiDevice.SectorSize);
                    Unreadable++;
                    session.Log(string.Format("{0}UNREADABLE sector LBA {1:N0} - replaced by silence ({2})", tag, lba + i, dev.ErrorText()));
                    if (Unreadable > MaxUnreadable) throw new IOException("too many unreadable sectors - the disc is damaged or dirty");
                }
            }
        }
    }

    internal enum VerifyResult { NoDisc, WrongDisc, TocOnly, Verified }

    /// <summary>Disc identity check used when resuming a paused rip.</summary>
    internal static class ResumeCheck
    {
        public const int HashWindow = 75;     // one second of audio

        /// <summary>SHA-1 of up to 75 sectors of PCM ending at the stop point (or the track start if nothing was ripped yet).</summary>
        public static string ComputePcmHash(ScsiDevice dev, TrackInfo t, int sector)
        {
            int count, start;
            if (sector > 0) { count = Math.Min(HashWindow, sector); start = t.StartLba + sector - count; }
            else { count = Math.Min(HashWindow, t.Sectors); start = t.StartLba; }

            byte[] all = new byte[count * ScsiDevice.SectorSize];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(DriveJob.ChunkSectors, count - done);
                bool ok = false;
                for (int a = 0; a < 3 && !ok; a++) ok = dev.ReadCdda(start + done, n, all, done * ScsiDevice.SectorSize);
                if (!ok) return "";
                done += n;
            }
            return Util.Sha1Hex(all);
        }

        public static VerifyResult Verify(PausedDrive pd, out DiscInfo disc, out string status)
        {
            disc = null;
            char letter = char.ToUpper(pd.Letter[0]);
            bool exists = false;
            foreach (DriveInfo di in DriveInfo.GetDrives())
                if (di.DriveType == DriveType.CDRom && char.ToUpper(di.Name[0]) == letter) exists = true;
            if (!exists) { status = "Drive " + letter + ": is not available."; return VerifyResult.NoDisc; }

            string err;
            ScsiDevice dev = ScsiDevice.Open(letter, out err);
            if (dev == null) { status = "Cannot open drive " + letter + ": (" + err + ")"; return VerifyResult.NoDisc; }
            try
            {
                if (!DiscReader.WaitReady(dev, 2000)) { status = "Waiting for a disc..."; return VerifyResult.NoDisc; }
                string name = dev.Inquiry();
                DiscInfo d = DiscReader.Read(dev, letter, string.IsNullOrEmpty(name) ? pd.DriveName : name);
                if (d == null) { status = "The inserted disc is not an audio CD."; return VerifyResult.WrongDisc; }
                if (d.TocHash != pd.TocHash) { status = "A different disc is in the drive."; return VerifyResult.WrongDisc; }
                disc = d;

                TrackInfo t = d.Tracks.Find(delegate(TrackInfo x) { return x.Number == pd.TrackNumber; });
                if (t == null) { status = "Disc layout does not contain the paused track."; return VerifyResult.WrongDisc; }
                if (pd.PcmHash.Length == 0) { status = "Disc matches (no PCM hash was saved)."; return VerifyResult.Verified; }

                string h = ComputePcmHash(dev, t, pd.SectorInTrack);
                if (h == pd.PcmHash) { status = "Verified - disc and PCM hash match."; return VerifyResult.Verified; }
                status = "Same disc layout, but the PCM hash differs.";
                return VerifyResult.TocOnly;
            }
            finally { dev.Dispose(); }
        }
    }
}
