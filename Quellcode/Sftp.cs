// Sync Pilot – SFTP-Client (Protokollversion 3, wie bei OpenSSH) über die eigene SSH-Verbindung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SyncPilot
{
    /// <summary>Fehlerantwort des SFTP-Servers (2 = nicht vorhanden, 3 = keine Berechtigung …).</summary>
    public class SftpException : Exception
    {
        public const uint NoSuchFile = 2;
        public readonly uint Code;
        public SftpException(uint code, string msg) : base(msg) { Code = code; }
    }

    public class SftpAttrs
    {
        public bool HasSize, HasPerm, HasTime;
        public long Size;
        public uint Perm;
        public uint Mtime;

        public bool IsDir { get { return HasPerm && (Perm & 0xF000) == 0x4000; } }
        public bool IsFile { get { return HasPerm && (Perm & 0xF000) == 0x8000; } }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime MtimeUtc { get { return Epoch.AddSeconds(Mtime); } }

        public static uint ToUnix(DateTime utc)
        {
            DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            double s = (u - Epoch).TotalSeconds;
            if (s < 0) return 0;
            if (s > uint.MaxValue) return uint.MaxValue;
            return (uint)s;
        }
    }

    public class SftpItem
    {
        public string Name;
        public SftpAttrs Attrs;
    }

    public sealed class SftpClient : IDisposable
    {
        public const int Chunk = 32768;
        public const int Depth = 16;

        readonly FtpUrl url;
        readonly string user, pass, trusted;
        SshClient ssh;
        uint nextId = 1;
        readonly Dictionary<uint, byte[]> pending = new Dictionary<uint, byte[]>();
        bool posixRename;

        /// <summary>Startordner nach der Anmeldung (absolut).</summary>
        public string Home { get; private set; }
        public bool Connected { get { return ssh != null && ssh.Connected; } }

        public SftpClient(FtpUrl url, string user, string pass, string trustedFingerprint)
        {
            this.url = url;
            this.user = string.IsNullOrEmpty(user) ? url.UserInUrl : user;
            this.pass = pass ?? "";
            trusted = trustedFingerprint;
            Home = "/";
        }

        public void Connect()
        {
            Close();
            pending.Clear();
            nextId = 1;
            SshClient c = new SshClient();
            try { c.Connect(url.Host, url.Port, user, pass, trusted); }
            catch (SocketException ex)
            {
                throw new SyncException("SFTP-Server " + url.Host + ":" + url.Port + " nicht erreichbar (" + ex.Message + ")");
            }
            ssh = c;
            try
            {
                byte[] init = new SshBuf().U32(5).Byte(1).U32(3).ToArray();
                ssh.Write(init, 0, init.Length);
                byte[] p = ReadPacket();
                if (p[0] != 2) throw new SshException("SFTP konnte nicht gestartet werden.");
                SshReader r = new SshReader(p, 1);
                uint version = r.U32();
                if (version < 3) throw new SshException("Der Server spricht nur SFTP-Version " + version + " (mindestens 3 nötig).");
                posixRename = false;
                while (!r.AtEnd)
                {
                    string name = Encoding.ASCII.GetString(r.Str());
                    r.Str();
                    if (name == "posix-rename@openssh.com") posixRename = true;
                }
                Home = RealPath(".");
            }
            catch
            {
                Close();
                throw;
            }
        }

        // ---------- Pakete ----------

        byte[] ReadPacket()
        {
            byte[] lb = new byte[4];
            ssh.Read(lb, 0, 4);
            uint n = new SshReader(lb, 0).U32();
            if (n < 5 || n > 4 * 1024 * 1024) throw new SshException("Ungültiges SFTP-Paket vom Server.");
            byte[] p = new byte[n];
            ssh.Read(p, 0, (int)n);
            return p;
        }

        internal uint Send(int type, SshBuf body)
        {
            if (ssh == null) throw new IOException("Keine Verbindung zum SFTP-Server.");
            uint id = nextId++;
            if (nextId == 0) nextId = 1;
            byte[] b = body.ToArray();
            byte[] pkt = new SshBuf().U32((uint)(b.Length + 5)).Byte(type).U32(id).Raw(b).ToArray();
            ssh.Write(pkt, 0, pkt.Length);
            return id;
        }

        internal byte[] Wait(uint id)
        {
            byte[] p;
            while (!pending.TryGetValue(id, out p))
            {
                if (ssh == null) throw new IOException("Keine Verbindung zum SFTP-Server.");
                byte[] q = ReadPacket();
                if (q[0] == 2) throw new SshException("Unerwartete SFTP-Antwort.");
                pending[new SshReader(q, 1).U32()] = q;
            }
            pending.Remove(id);
            return p;
        }

        byte[] Call(int type, SshBuf body) { return Wait(Send(type, body)); }

        /// <summary>Wertet die Antwort aus: Fehler als SftpException, sonst Leser hinter der Anfrage-Nummer (oder null bei OK).</summary>
        internal static SshReader Check(byte[] p, int okType)
        {
            if (p[0] == 101)
            {
                SshReader r = new SshReader(p, 5);
                uint code = r.U32();
                string msg = "";
                try { msg = r.Text(); } catch { }
                if (code == 0 && okType == 0) return null;
                throw new SftpException(code, Describe(code, msg));
            }
            if (okType != 0 && p[0] == okType) return new SshReader(p, 5);
            throw new SshException("Unerwartete SFTP-Antwort (" + p[0] + ").");
        }

        static string Describe(uint code, string msg)
        {
            string de;
            switch (code)
            {
                case 1: de = "Dateiende"; break;
                case 2: de = "Nicht vorhanden"; break;
                case 3: de = "Keine Berechtigung"; break;
                case 4: de = "Vorgang fehlgeschlagen"; break;
                case 8: de = "Vom Server nicht unterstützt"; break;
                default: de = "SFTP-Fehler " + code; break;
            }
            return msg.Length > 0 ? de + " (" + msg + ")" : de;
        }

        static SshBuf PathBuf(string path) { return new SshBuf().Str(path); }

        internal static SftpAttrs ReadAttrs(SshReader r)
        {
            SftpAttrs a = new SftpAttrs();
            uint fl = r.U32();
            if ((fl & 1) != 0) { a.HasSize = true; a.Size = (long)r.U64(); }
            if ((fl & 2) != 0) { r.U32(); r.U32(); }
            if ((fl & 4) != 0) { a.HasPerm = true; a.Perm = r.U32(); }
            if ((fl & 8) != 0) { r.U32(); a.HasTime = true; a.Mtime = r.U32(); }
            if ((fl & 0x80000000) != 0)
            {
                uint n = r.U32();
                for (uint i = 0; i < n; i++) { r.Str(); r.Str(); }
            }
            return a;
        }

        // ---------- Befehle ----------

        public string RealPath(string path)
        {
            SshReader r = Check(Call(16, PathBuf(path)), 104);
            if (r.U32() < 1) throw new SshException("Ungültige Antwort auf REALPATH.");
            return r.Text();
        }

        public SftpAttrs Stat(string path)
        {
            return ReadAttrs(Check(Call(17, PathBuf(path)), 105));
        }

        /// <summary>Wie Stat, aber null, wenn es den Eintrag nicht gibt.</summary>
        public SftpAttrs TryStat(string path)
        {
            try { return Stat(path); }
            catch (SftpException ex)
            {
                if (ex.Code == SftpException.NoSuchFile) return null;
                throw;
            }
        }

        public bool DirExists(string path)
        {
            SftpAttrs a = TryStat(path);
            return a != null && a.IsDir;
        }

        /// <summary>Ordnerinhalt ohne "." und "..". incomplete: Namen, die nicht als UTF-8 lesbar waren, fehlen.</summary>
        public List<SftpItem> List(string path, out bool incomplete)
        {
            incomplete = false;
            UTF8Encoding strict = new UTF8Encoding(false, true);
            byte[] handle = Check(Call(11, PathBuf(path)), 102).Str();
            List<SftpItem> items = new List<SftpItem>();
            bool ok = false;
            try
            {
                while (true)
                {
                    byte[] p = Call(12, new SshBuf().Str(handle));
                    if (p[0] == 101 && new SshReader(p, 5).U32() == 1) break; // Ende der Liste
                    SshReader r = Check(p, 104);
                    uint n = r.U32();
                    for (uint i = 0; i < n; i++)
                    {
                        byte[] raw = r.Str();
                        r.Str(); // Langform wie bei "ls -l"
                        SftpAttrs a = ReadAttrs(r);
                        string name;
                        try { name = strict.GetString(raw); }
                        catch (ArgumentException) { incomplete = true; continue; }
                        if (name == "." || name == "..") continue;
                        items.Add(new SftpItem { Name = name, Attrs = a });
                    }
                }
                ok = true;
            }
            finally
            {
                try { Check(Call(4, new SshBuf().Str(handle)), 0); }
                catch { if (ok) throw; }
            }
            return items;
        }

        public List<SftpItem> List(string path)
        {
            bool inc;
            return List(path, out inc);
        }

        public void MakeDir(string path) { Check(Call(14, PathBuf(path).U32(0)), 0); }
        public void RemoveDir(string path) { Check(Call(15, PathBuf(path)), 0); }
        public void Remove(string path) { Check(Call(13, PathBuf(path)), 0); }

        /// <summary>Umbenennen; ein vorhandenes Ziel wird ersetzt.</summary>
        public void Rename(string from, string to)
        {
            if (posixRename)
            {
                Check(Call(200, new SshBuf().Str("posix-rename@openssh.com").Str(from).Str(to)), 0);
                return;
            }
            try { Remove(to); }
            catch (SftpException ex) { if (ex.Code != SftpException.NoSuchFile) throw; }
            Check(Call(18, new SshBuf().Str(from).Str(to)), 0);
        }

        public void SetTime(string path, DateTime utc)
        {
            uint t = SftpAttrs.ToUnix(utc);
            Check(Call(9, PathBuf(path).U32(8).U32(t).U32(t)), 0);
        }

        internal byte[] Open(string path, uint flags)
        {
            return Check(Call(3, PathBuf(path).U32(flags).U32(0)), 102).Str();
        }

        internal void CloseHandle(byte[] handle)
        {
            Check(Call(4, new SshBuf().Str(handle)), 0);
        }

        public Stream OpenRead(string path)
        {
            return new SftpReadStream(this, Open(path, 1));
        }

        /// <summary>Datei hochladen (mehrere Pakete gleichzeitig unterwegs, damit es auch über weite Strecken schnell geht).</summary>
        public void Upload(string path, Stream src)
        {
            byte[] handle = Open(path, 2 | 8 | 0x10); // WRITE | CREAT | TRUNC
            Queue<uint> queue = new Queue<uint>();
            bool ok = false;
            try
            {
                byte[] buf = new byte[Chunk];
                ulong off = 0;
                bool done = false;
                while (true)
                {
                    while (!done && queue.Count < Depth)
                    {
                        int n = ReadFull(src, buf);
                        if (n == 0) { done = true; break; }
                        queue.Enqueue(Send(6, new SshBuf().Str(handle).U64(off).Str(buf, 0, n)));
                        off += (ulong)n;
                        if (n < buf.Length) done = true;
                    }
                    if (queue.Count == 0) break;
                    Check(Wait(queue.Dequeue()), 0);
                }
                ok = true;
            }
            finally
            {
                try
                {
                    while (queue.Count > 0) Wait(queue.Dequeue());
                    CloseHandle(handle);
                }
                catch { if (ok) throw; }
            }
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public void Close()
        {
            if (ssh != null) ssh.Close();
            ssh = null;
            pending.Clear();
        }

        public void Dispose() { Close(); }
    }

    /// <summary>Lesestrom einer Datei auf dem SFTP-Server, mit mehreren vorausgeschickten Leseanfragen.</summary>
    public sealed class SftpReadStream : Stream
    {
        struct Req { public uint Id; public ulong Offset; public int Len; }

        readonly SftpClient c;
        readonly byte[] handle;
        readonly Queue<Req> queue = new Queue<Req>();
        ulong nextOffset, pos;
        bool eof, closed;
        byte[] cur;
        int curPos;

        internal SftpReadStream(SftpClient client, byte[] handle)
        {
            c = client;
            this.handle = handle;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            while (cur == null || curPos >= cur.Length)
                if (!Fill()) return 0;
            int n = Math.Min(count, cur.Length - curPos);
            Buffer.BlockCopy(cur, curPos, buffer, offset, n);
            curPos += n;
            return n;
        }

        bool Fill()
        {
            if (closed) return false;
            while (!eof && queue.Count < SftpClient.Depth)
            {
                Req r = new Req { Offset = nextOffset, Len = SftpClient.Chunk };
                r.Id = c.Send(5, new SshBuf().Str(handle).U64(nextOffset).U32((uint)SftpClient.Chunk));
                queue.Enqueue(r);
                nextOffset += SftpClient.Chunk;
            }
            if (queue.Count == 0) return false;
            Req q = queue.Dequeue();
            byte[] p = c.Wait(q.Id);
            if (p[0] == 101)
            {
                SshReader sr = new SshReader(p, 5);
                if (sr.U32() == 1) // Dateiende
                {
                    eof = true;
                    Drain();
                    return false;
                }
                SftpClient.Check(p, 103); // wirft mit Fehlermeldung
            }
            byte[] data = SftpClient.Check(p, 103).Str();
            if (q.Offset != pos) throw new SshException("SFTP: Daten in falscher Reihenfolge.");
            if (data.Length == 0) { eof = true; Drain(); return false; }
            cur = data;
            curPos = 0;
            pos += (ulong)data.Length;
            if (data.Length < q.Len)
            {
                // Server hat weniger geliefert als angefragt: vorausgeschickte Anfragen verwerfen, ab hier neu anfragen
                Drain();
                nextOffset = pos;
            }
            return true;
        }

        void Drain()
        {
            while (queue.Count > 0) c.Wait(queue.Dequeue().Id);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closed)
            {
                closed = true;
                try
                {
                    Drain();
                    c.CloseHandle(handle);
                }
                catch { }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { return (long)pos; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
