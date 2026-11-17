using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CdRipper
{
    internal struct RawTocEntry
    {
        public int Number;     // 1..99, or 0xAA for the lead-out
        public int Adr;
        public int Control;    // bit 2 set = data track
        public int Lba;
        public bool IsAudio { get { return (Control & 0x04) == 0; } }
    }

    /// <summary>
    /// Talks to a CD/DVD drive with IOCTL_SCSI_PASS_THROUGH_DIRECT (Windows SCSI CD support).
    /// The SCSI_PASS_THROUGH_DIRECT structure is laid out by hand so it is correct on both
    /// x86 (44 bytes) and x64 (56 bytes).
    /// </summary>
    internal sealed class ScsiDevice : IDisposable
    {
        public const int SectorSize = 2352;        // raw CD-DA sector
        public const int MaxTransfer = 65536;
        private const int SenseLen = 32;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;

        // Offsets inside SCSI_PASS_THROUGH_DIRECT (depend on pointer size)
        private static readonly int OffDataBuffer = (IntPtr.Size == 8) ? 24 : 20;
        private static readonly int OffSenseOffset = OffDataBuffer + IntPtr.Size;
        private static readonly int OffCdb = OffSenseOffset + 4;
        private static readonly int SptSize = (OffCdb + 16 + IntPtr.Size - 1) & ~(IntPtr.Size - 1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
            IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
            out uint bytesReturned, IntPtr overlapped);

        private readonly SafeFileHandle handle;
        private readonly IntPtr sptBuf;
        private readonly IntPtr dataRaw;
        private readonly IntPtr dataBuf;      // 4 KiB-aligned transfer buffer
        private bool disposed;

        public readonly char Letter;
        public byte SenseKey;
        public byte Asc;
        public byte Ascq;
        public int Win32Error;

        private ScsiDevice(SafeFileHandle h, char letter)
        {
            handle = h;
            Letter = letter;
            sptBuf = Marshal.AllocHGlobal(SptSize + SenseLen);
            dataRaw = Marshal.AllocHGlobal(MaxTransfer + 4096);
            dataBuf = new IntPtr((dataRaw.ToInt64() + 4095) & ~4095L);
        }

        public static ScsiDevice Open(char letter, out string error)
        {
            string path = "\\\\.\\" + char.ToUpper(letter) + ":";
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }
            error = null;
            return new ScsiDevice(h, char.ToUpper(letter));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            handle.Close();
            Marshal.FreeHGlobal(sptBuf);
            Marshal.FreeHGlobal(dataRaw);
        }

        // ------------------------------------------------------------------ core
        private bool Exec(byte[] cdb, int dataLen, bool dataIn, int timeoutSec)
        {
            int total = SptSize + SenseLen;
            for (int i = 0; i < total; i += 4) Marshal.WriteInt32(sptBuf, i, 0);

            Marshal.WriteInt16(sptBuf, 0, (short)SptSize);                 // Length
            Marshal.WriteByte(sptBuf, 6, (byte)cdb.Length);                // CdbLength
            Marshal.WriteByte(sptBuf, 7, (byte)SenseLen);                  // SenseInfoLength
            Marshal.WriteByte(sptBuf, 8, (byte)(dataLen == 0 ? 2 : (dataIn ? 1 : 0))); // DataIn
            Marshal.WriteInt32(sptBuf, 12, dataLen);                       // DataTransferLength
            Marshal.WriteInt32(sptBuf, 16, timeoutSec);                    // TimeOutValue
            Marshal.WriteIntPtr(sptBuf, OffDataBuffer, dataLen == 0 ? IntPtr.Zero : dataBuf);
            Marshal.WriteInt32(sptBuf, OffSenseOffset, SptSize);           // sense data follows the struct
            for (int i = 0; i < cdb.Length; i++) Marshal.WriteByte(sptBuf, OffCdb + i, cdb[i]);

            SenseKey = 0; Asc = 0; Ascq = 0; Win32Error = 0;
            uint returned;
            bool ok = DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH_DIRECT,
                sptBuf, (uint)total, sptBuf, (uint)total, out returned, IntPtr.Zero);
            if (!ok)
            {
                Win32Error = Marshal.GetLastWin32Error();
                SenseKey = 0xFF;
                return false;
            }
            if (Marshal.ReadByte(sptBuf, 2) == 0) return true;             // ScsiStatus GOOD

            int rc = Marshal.ReadByte(sptBuf, SptSize) & 0x7F;
            if (rc == 0x72 || rc == 0x73)
            {
                SenseKey = (byte)(Marshal.ReadByte(sptBuf, SptSize + 1) & 0x0F);
                Asc = Marshal.ReadByte(sptBuf, SptSize + 2);
                Ascq = Marshal.ReadByte(sptBuf, SptSize + 3);
            }
            else
            {
                SenseKey = (byte)(Marshal.ReadByte(sptBuf, SptSize + 2) & 0x0F);
                Asc = Marshal.ReadByte(sptBuf, SptSize + 12);
                Ascq = Marshal.ReadByte(sptBuf, SptSize + 13);
            }
            if (SenseKey == 0) SenseKey = 0xFE;
            return false;
        }

        private static readonly string[] SenseNames = {
            "No Sense", "Recovered Error", "Not Ready", "Medium Error", "Hardware Error",
            "Illegal Request", "Unit Attention", "Data Protect", "Blank Check", "Vendor Specific",
            "Copy Aborted", "Aborted Command", "Equal", "Volume Overflow", "Miscompare", "Reserved" };

        public string ErrorText()
        {
            if (SenseKey == 0xFF) return new Win32Exception(Win32Error).Message;
            if (SenseKey == 0xFE) return "SCSI check condition without sense data";
            return string.Format("SCSI sense {0:X}/{1:X2}/{2:X2} ({3})", SenseKey, Asc, Ascq, SenseNames[SenseKey & 0x0F]);
        }

        /// <summary>True when the failure means the disc left the drive or was swapped.</summary>
        public bool MediaGone
        {
            get { return (SenseKey == 2 && Asc == 0x3A) || (SenseKey == 6 && Asc == 0x28); }
        }

        // ------------------------------------------------------------------ commands
        public bool TestUnitReady()
        {
            return Exec(new byte[6], 0, false, 5);
        }

        /// <summary>INQUIRY: returns "VENDOR PRODUCT", e.g. "PHILIPS DVD+-RW SDVD8441".</summary>
        public string Inquiry()
        {
            byte[] cdb = new byte[6];
            cdb[0] = 0x12; cdb[4] = 36;
            if (!Exec(cdb, 36, true, 5)) return null;
            byte[] d = new byte[36];
            Marshal.Copy(dataBuf, d, 0, 36);
            string vendor = Encoding.ASCII.GetString(d, 8, 8).Trim();
            string product = Encoding.ASCII.GetString(d, 16, 16).Trim();
            return (vendor + " " + product).Trim();
        }

        private byte[] ReadTocChunk(int format, bool msf, int len)
        {
            byte[] cdb = new byte[10];
            cdb[0] = 0x43;                       // READ TOC/PMA/ATIP
            cdb[1] = (byte)(msf ? 0x02 : 0x00);
            cdb[2] = (byte)format;
            cdb[7] = (byte)(len >> 8);
            cdb[8] = (byte)len;
            if (!Exec(cdb, len, true, 10)) return null;
            byte[] d = new byte[len];
            Marshal.Copy(dataBuf, d, 0, len);
            return d;
        }

        /// <summary>READ TOC/PMA/ATIP in any format (0 = TOC, 5 = CD-TEXT). Header first, then everything.</summary>
        public byte[] ReadTocCommand(int format, bool msf)
        {
            byte[] hdr = ReadTocChunk(format, msf, 4);
            if (hdr == null) return null;
            int len = ((hdr[0] << 8) | hdr[1]) + 2;
            if (len <= 4) return hdr;
            if (len > MaxTransfer) len = MaxTransfer;
            return ReadTocChunk(format, msf, len);
        }

        public List<RawTocEntry> ReadToc()
        {
            byte[] d = ReadTocCommand(0, false);       // LBA addressing
            if (d == null || d.Length < 4) return null;
            int end = Math.Min(((d[0] << 8) | d[1]) + 2, d.Length);
            List<RawTocEntry> list = new List<RawTocEntry>();
            for (int off = 4; off + 8 <= end; off += 8)
            {
                RawTocEntry e = new RawTocEntry();
                e.Adr = d[off + 1] >> 4;
                e.Control = d[off + 1] & 0x0F;
                e.Number = d[off + 2];
                e.Lba = (d[off + 4] << 24) | (d[off + 5] << 16) | (d[off + 6] << 8) | d[off + 7];
                list.Add(e);
            }
            return list;
        }

        /// <summary>SET CD SPEED with 0xFFFF = fastest the drive supports.</summary>
        public bool SetMaxSpeed()
        {
            byte[] cdb = new byte[12];
            cdb[0] = 0xBB;
            cdb[2] = 0xFF; cdb[3] = 0xFF;      // read speed
            cdb[4] = 0xFF; cdb[5] = 0xFF;      // write speed
            return Exec(cdb, 0, false, 10);
        }

        /// <summary>Max read speed (kB/s) from the CD capabilities mode page 2Ah, or 0 if unknown.</summary>
        public int GetMaxReadSpeedKBps()
        {
            byte[] cdb = new byte[10];
            cdb[0] = 0x5A;                      // MODE SENSE(10)
            cdb[1] = 0x08;                      // DBD
            cdb[2] = 0x2A;                      // CD capabilities page
            cdb[7] = 0; cdb[8] = 64;
            if (!Exec(cdb, 64, true, 5)) return 0;
            byte[] d = new byte[64];
            Marshal.Copy(dataBuf, d, 0, 64);
            int pageOff = 8 + ((d[6] << 8) | d[7]);
            if (pageOff + 10 > d.Length) return 0;
            if ((d[pageOff] & 0x3F) != 0x2A) return 0;
            return (d[pageOff + 8] << 8) | d[pageOff + 9];
        }

        /// <summary>READ CD: raw 2352-byte CD-DA user data (PCM) for 'count' sectors starting at 'lba'.</summary>
        public bool ReadCdda(int lba, int count, byte[] dest, int destOffset)
        {
            byte[] cdb = new byte[12];
            cdb[0] = 0xBE;
            cdb[1] = 0x04;                                  // expected sector type: CD-DA
            cdb[2] = (byte)(lba >> 24);
            cdb[3] = (byte)(lba >> 16);
            cdb[4] = (byte)(lba >> 8);
            cdb[5] = (byte)lba;
            cdb[6] = (byte)(count >> 16);
            cdb[7] = (byte)(count >> 8);
            cdb[8] = (byte)count;
            cdb[9] = 0x10;                                  // user data only
            int len = count * SectorSize;
            if (!Exec(cdb, len, true, 10)) return false;
            Marshal.Copy(dataBuf, dest, destOffset, len);
            return true;
        }

        public bool Eject()
        {
            byte[] cdb = new byte[6];
            cdb[0] = 0x1B;                      // START STOP UNIT
            cdb[4] = 0x02;                      // LoEj=1, Start=0
            return Exec(cdb, 0, false, 15);
        }

        public static void TryEject(char letter)
        {
            string err;
            ScsiDevice d = Open(letter, out err);
            if (d == null) return;
            try { d.Eject(); } finally { d.Dispose(); }
        }
    }
}
