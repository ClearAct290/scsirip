using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CdRipper
{
    /// <summary>Small helpers: time/size formatting, file names, hashing.</summary>
    internal static class Util
    {
        public static string Time(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--:--";
            int s = (int)Math.Round(seconds);
            int h = s / 3600, m = (s / 60) % 60, sec = s % 60;
            if (h > 0) return string.Format("{0}:{1:00}:{2:00}", h, m, sec);
            return string.Format("{0:00}:{1:00}", m, sec);
        }

        /// <summary>Sector count as MM:SS.FF (75 frames per second).</summary>
        public static string Msf(int sectors)
        {
            int total = sectors / 75;
            return string.Format("{0:00}:{1:00}.{2:00}", total / 60, total % 60, sectors % 75);
        }

        public static string Size(long bytes)
        {
            return string.Format("{0:N0} bytes ({1:0.0} MiB)", bytes, bytes / 1048576.0);
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 3) + "...";
        }

        public static string SafeFileName(string s)
        {
            if (s == null) return "";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 100) r = r.Substring(0, 100).TrimEnd();
            return r;
        }

        /// <summary>"01 - Title.wav" when the title is known, otherwise "Track 01.wav".</summary>
        public static string TrackFileName(TrackInfo t)
        {
            string title = SafeFileName(t.Title);
            if (title.Length > 0) return string.Format("{0:00} - {1}.wav", t.Number, title);
            return string.Format("Track {0:00}.wav", t.Number);
        }

        public static string Sha1Hex(byte[] data)
        {
            using (SHA1 sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            }
        }

        public static string Sha1Hex(string text)
        {
            return Sha1Hex(Encoding.UTF8.GetBytes(text));
        }
    }
}
