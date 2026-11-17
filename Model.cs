using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CdRipper
{
    internal sealed class TrackInfo
    {
        public int Number;
        public int StartLba;
        public int Sectors;
        public string Title = "";
        public string Performer = "";
    }

    internal sealed class DiscInfo
    {
        public char Letter;
        public string DriveName = "";
        public List<TrackInfo> Tracks = new List<TrackInfo>();
        public string AlbumTitle = "";
        public string AlbumPerformer = "";
        public bool HasCdText;
        public string TocHash = "";

        public int TotalSectors
        {
            get
            {
                int s = 0;
                foreach (TrackInfo t in Tracks) s += t.Sectors;
                return s;
            }
        }

        /// <summary>Short description of the disc, e.g. for "insert this disc" prompts.</summary>
        public string Excerpt(int max)
        {
            string s = "";
            if (HasCdText)
            {
                if (AlbumPerformer.Length > 0 && AlbumTitle.Length > 0) s = AlbumPerformer + " - " + AlbumTitle;
                else if (AlbumTitle.Length > 0) s = AlbumTitle;
                else if (AlbumPerformer.Length > 0) s = AlbumPerformer;
                else
                {
                    foreach (TrackInfo t in Tracks)
                        if (t.Title.Length > 0) { s = t.Title; break; }
                }
            }
            if (s.Length == 0)
                s = string.Format("Audio CD, {0} tracks, {1}", Tracks.Count, Util.Time(TotalSectors / 75.0));
            return Util.Truncate(s, max);
        }
    }

    internal static class DiscReader
    {
        /// <summary>Waits for the unit to become ready. False = no disc (or no SCSI support).</summary>
        public static bool WaitReady(ScsiDevice dev, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                if (dev.TestUnitReady()) return true;
                if (dev.SenseKey == 0xFF) return false;                    // not a SCSI-capable device
                if (dev.SenseKey == 2 && dev.Asc == 0x3A) return false;    // medium not present
                if (waited >= timeoutMs) return false;
                Thread.Sleep(300);
                waited += 300;
            }
        }

        /// <summary>Reads the TOC (+ CD-TEXT). Returns null if the disc has no audio tracks.</summary>
        public static DiscInfo Read(ScsiDevice dev, char letter, string driveName)
        {
            List<RawTocEntry> toc = dev.ReadToc();
            if (toc == null || toc.Count < 2) return null;
            toc.Sort(delegate(RawTocEntry a, RawTocEntry b) { return a.Number.CompareTo(b.Number); });
            if (toc[toc.Count - 1].Number != 0xAA) return null;

            DiscInfo disc = new DiscInfo();
            disc.Letter = letter;
            disc.DriveName = driveName;
            StringBuilder sig = new StringBuilder();

            for (int i = 0; i < toc.Count; i++)
            {
                RawTocEntry e = toc[i];
                sig.Append(e.Number).Append(':').Append(e.Control).Append(':').Append(e.Lba).Append(';');
                if (e.Number == 0xAA || !e.IsAudio) continue;

                RawTocEntry next = toc[i + 1];
                int end = next.Lba;
                // CD-Extra / Enhanced CD: a data session follows the audio; skip the 11400-sector gap
                if (next.Number != 0xAA && !next.IsAudio) end -= 11400;
                int sectors = end - e.Lba;
                if (sectors < 1) continue;

                TrackInfo t = new TrackInfo();
                t.Number = e.Number;
                t.StartLba = e.Lba;
                t.Sectors = sectors;
                disc.Tracks.Add(t);
            }
            if (disc.Tracks.Count == 0) return null;
            disc.TocHash = Util.Sha1Hex(sig.ToString());

            CdText.Apply(disc, dev.ReadTocCommand(5, false));
            return disc;
        }
    }

    /// <summary>Parses CD-TEXT packs (READ TOC format 5) into titles and performers.</summary>
    internal static class CdText
    {
        public static void Apply(DiscInfo disc, byte[] d)
        {
            if (d == null || d.Length < 22) return;
            int total = Math.Min(((d[0] << 8) | d[1]) + 2, d.Length);

            // 18-byte packs; use the first language block, drop repeated sequence numbers
            List<int> packs = new List<int>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            for (int off = 4; off + 18 <= total; off += 18)
            {
                if (((d[off + 3] >> 4) & 7) != 0) continue;
                int seq = d[off + 2];
                if (seen.ContainsKey(seq)) continue;
                seen[seq] = true;
                packs.Add(off);
            }

            Dictionary<int, string> titles = Strings(d, packs, 0x80);
            Dictionary<int, string> perf = Strings(d, packs, 0x81);
            if (titles.Count == 0 && perf.Count == 0) return;

            string s;
            if (titles.TryGetValue(0, out s)) disc.AlbumTitle = s;
            if (perf.TryGetValue(0, out s)) disc.AlbumPerformer = s;
            bool any = disc.AlbumTitle.Length > 0 || disc.AlbumPerformer.Length > 0;
            foreach (TrackInfo t in disc.Tracks)
            {
                if (titles.TryGetValue(t.Number, out s)) { t.Title = s; if (s.Length > 0) any = true; }
                if (perf.TryGetValue(t.Number, out s)) t.Performer = s;
            }
            disc.HasCdText = any;
        }

        private static Dictionary<int, string> Strings(byte[] d, List<int> packs, byte type)
        {
            Dictionary<int, string> res = new Dictionary<int, string>();
            StringBuilder sb = new StringBuilder();
            int track = 0;
            foreach (int off in packs)
            {
                if (d[off] != type) continue;
                if ((d[off + 3] & 0x80) != 0) return new Dictionary<int, string>();  // double-byte text: unsupported
                track = d[off + 1] & 0x7F;
                for (int i = 0; i < 12; i++)
                {
                    byte b = d[off + 4 + i];
                    if (b == 0)
                    {
                        string raw = sb.ToString();
                        sb.Length = 0;
                        string s;
                        if (raw == "\t")
                        {
                            if (!res.TryGetValue(track - 1, out s)) s = "";   // TAB = same as previous
                        }
                        else s = raw.Trim();
                        res[track] = s;
                        track++;
                    }
                    else sb.Append((char)b);                                  // ISO 8859-1
                }
            }
            return res;
        }
    }
}
