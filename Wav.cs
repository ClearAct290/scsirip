using System;
using System.IO;
using System.Text;

namespace CdRipper
{
    /// <summary>
    /// Builds the 44-byte RIFF/WAVE header (CD audio: 44.1 kHz, 16-bit, stereo) and the
    /// optional "id3 " chunk that carries an ID3v2.3 tag.
    /// The header is written with the FINAL sizes up-front, so a stopped (aborted/paused)
    /// file is never touched afterwards: it stays exactly as it was when the rip stopped.
    /// </summary>
    internal static class WavFile
    {
        public const int HeaderSize = 44;

        public static byte[] BuildHeader(long dataLen, int extraChunkBytes)
        {
            MemoryStream ms = new MemoryStream(HeaderSize);
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)(36 + dataLen + extraChunkBytes));
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write((uint)16);        // fmt chunk size
            w.Write((ushort)1);       // PCM
            w.Write((ushort)2);       // channels
            w.Write((uint)44100);     // sample rate
            w.Write((uint)176400);    // byte rate
            w.Write((ushort)4);       // block align
            w.Write((ushort)16);      // bits per sample
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((uint)dataLen);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Wraps an ID3v2 tag in a RIFF "id3 " chunk (padded to an even length).</summary>
        public static byte[] BuildId3Chunk(byte[] tag)
        {
            int pad = tag.Length & 1;
            byte[] c = new byte[8 + tag.Length + pad];
            c[0] = (byte)'i'; c[1] = (byte)'d'; c[2] = (byte)'3'; c[3] = (byte)' ';
            c[4] = (byte)tag.Length;
            c[5] = (byte)(tag.Length >> 8);
            c[6] = (byte)(tag.Length >> 16);
            c[7] = (byte)(tag.Length >> 24);
            Array.Copy(tag, 0, c, 8, tag.Length);
            return c;
        }

        /// <summary>ID3v2.3 tag: TIT2 (title) always, plus TPE1/TALB/TRCK when known.</summary>
        public static byte[] BuildId3(string title, string artist, string album, int track, int total)
        {
            MemoryStream frames = new MemoryStream();
            AddFrame(frames, "TIT2", title);
            if (!string.IsNullOrEmpty(artist)) AddFrame(frames, "TPE1", artist);
            if (!string.IsNullOrEmpty(album)) AddFrame(frames, "TALB", album);
            AddFrame(frames, "TRCK", track + "/" + total);
            byte[] fb = frames.ToArray();

            byte[] tag = new byte[10 + fb.Length];
            tag[0] = (byte)'I'; tag[1] = (byte)'D'; tag[2] = (byte)'3';
            tag[3] = 3; tag[4] = 0;          // version 2.3.0
            tag[5] = 0;                      // flags
            int n = fb.Length;               // syncsafe size
            tag[6] = (byte)((n >> 21) & 0x7F);
            tag[7] = (byte)((n >> 14) & 0x7F);
            tag[8] = (byte)((n >> 7) & 0x7F);
            tag[9] = (byte)(n & 0x7F);
            Array.Copy(fb, 0, tag, 10, fb.Length);
            return tag;
        }

        private static void AddFrame(MemoryStream ms, string id, string text)
        {
            byte[] txt = Encoding.Unicode.GetBytes(text);       // UTF-16LE
            byte[] body = new byte[1 + 2 + txt.Length + 2];
            body[0] = 1;                                        // encoding: UTF-16 with BOM
            body[1] = 0xFF; body[2] = 0xFE;                     // BOM (little endian)
            Array.Copy(txt, 0, body, 3, txt.Length);            // last two bytes stay 00 00 (terminator)

            byte[] idb = Encoding.ASCII.GetBytes(id);
            ms.Write(idb, 0, 4);
            ms.WriteByte((byte)(body.Length >> 24));            // ID3v2.3: plain big-endian size
            ms.WriteByte((byte)(body.Length >> 16));
            ms.WriteByte((byte)(body.Length >> 8));
            ms.WriteByte((byte)body.Length);
            ms.WriteByte(0); ms.WriteByte(0);                   // frame flags
            ms.Write(body, 0, body.Length);
        }
    }
}
