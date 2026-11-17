using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace CdRipper
{
    internal static class AppData
    {
        public static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CdRipper");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }
        public static string PausedFile { get { return Path.Combine(Dir, "paused_rip.xml"); } }
        public static string SettingsFile { get { return Path.Combine(Dir, "settings.ini"); } }
        public static string LogFile { get { return Path.Combine(Dir, "last_rip_log.txt"); } }
    }

    /// <summary>Tiny key=value settings file (folder and checkbox per drive letter).</summary>
    internal static class Settings
    {
        private static Dictionary<string, string> map;

        private static void Load()
        {
            map = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(AppData.SettingsFile)) return;
                foreach (string line in File.ReadAllLines(AppData.SettingsFile))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            if (map == null) Load();
            string v;
            return map.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            if (map == null) Load();
            map[key] = value;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in map) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(AppData.SettingsFile, lines.ToArray());
            }
            catch { }
        }
    }

    // ---- paused-rip state (serialised to %APPDATA%\CdRipper\paused_rip.xml) ----
    public class PausedDrive
    {
        public string Letter = "";
        public string DriveName = "";
        public string Excerpt = "";       // shortened CD-TEXT / description shown on the resume screen
        public string Folder = "";
        public string TocHash = "";
        public int TrackNumber;           // track being ripped when paused
        public int SectorInTrack;         // first sector NOT yet written
        public string PcmHash = "";       // SHA-1 of the PCM window just before the stop point
    }

    public class PausedState
    {
        public string SavedUtc = "";
        public bool EjectWhenDone;
        public List<PausedDrive> Drives = new List<PausedDrive>();
    }

    internal static class PausedStore
    {
        public static bool Exists() { return File.Exists(AppData.PausedFile); }

        public static void Save(PausedState s)
        {
            string tmp = AppData.PausedFile + ".tmp";
            XmlSerializer xs = new XmlSerializer(typeof(PausedState));
            using (FileStream fs = File.Create(tmp)) xs.Serialize(fs, s);
            if (File.Exists(AppData.PausedFile)) File.Delete(AppData.PausedFile);   // overwrite older paused progress
            File.Move(tmp, AppData.PausedFile);
        }

        public static PausedState Load()
        {
            try
            {
                if (!File.Exists(AppData.PausedFile)) return null;
                XmlSerializer xs = new XmlSerializer(typeof(PausedState));
                using (FileStream fs = File.OpenRead(AppData.PausedFile))
                    return (PausedState)xs.Deserialize(fs);
            }
            catch { return null; }
        }

        public static void Delete()
        {
            try { if (File.Exists(AppData.PausedFile)) File.Delete(AppData.PausedFile); } catch { }
        }
    }
}
