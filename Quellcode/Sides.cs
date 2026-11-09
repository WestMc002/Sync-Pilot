// Sync Pilot – Seiten A und B: Windows-Ordner (lokal, Netzwerk), FTP- oder SFTP-Server
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace SyncPilot
{
    /// <summary>Eine Seite des Abgleichs. Pfade sind relativ zur Wurzel und verwenden "\" als Trenner.</summary>
    public interface IFileSide : IDisposable
    {
        string Display { get; }
        bool IsRemote { get; }
        /// <summary>Wirft SyncException, wenn die Wurzel nicht erreichbar ist.</summary>
        void CheckRoot();
        Dictionary<string, FileEntry> Scan(SyncJob job, Matcher m, Func<bool> cancel, Action<int> progress,
            HashSet<string> unreadable, List<string> errors);
        Stream OpenRead(string rel);
        /// <summary>Schreibt die Datei von der anderen Seite hierher und liefert den neuen Stand auf dieser Seite.</summary>
        FileEntry WriteFile(string rel, IFileSide from, FileEntry src);
        void CreateDir(string rel);
        /// <summary>Löscht eine Datei. Liefert, wie gelöscht wurde, oder null, wenn sie nicht mehr da war.</summary>
        string DeleteFile(string rel, bool recycle);
        /// <summary>Löscht einen leeren Ordner. Liefert null, wenn er nicht leer oder nicht vorhanden ist.</summary>
        string DeleteDir(string rel, bool recycle);
        /// <summary>Datei bzw. Ordner umbenennen oder verschieben (Ziel darf nicht existieren; fehlende Oberordner werden angelegt).</summary>
        void Move(string fromRel, string toRel, bool isDir);
        /// <summary>true, wenn diese Seite Dateien mit Punkt am Anfang (.htaccess) womöglich nicht anzeigt.</summary>
        bool HidesDotFiles { get; }
    }

    public static class Sides
    {
        public static IFileSide Open(SyncJob job, bool sideA)
        {
            string path = sideA ? job.FolderA : job.FolderB;
            string label = sideA ? "A" : "B";
            if (FtpUrl.IsSftp(path))
                return new SftpSide(path, label, sideA ? job.UserA : job.UserB, Secret.Unprotect(sideA ? job.PassA : job.PassB),
                    sideA ? job.CertA : job.CertB);
            if (FtpUrl.IsFtp(path))
                return new FtpSide(path, label, sideA ? job.UserA : job.UserB, Secret.Unprotect(sideA ? job.PassA : job.PassB),
                    sideA ? job.CertA : job.CertB);
            return new LocalSide(path, label);
        }

        /// <summary>Liest den Datenstrom bis zum Ende ab und schließt FTP-Übertragungen sauber ab.</summary>
        public static void Finish(Stream s)
        {
            FtpData d = s as FtpData;
            if (d != null) d.Finish();
        }
    }

    // =====================================================================

    public sealed class LocalSide : IFileSide
    {
        readonly string root, label;

        public LocalSide(string root, string label)
        {
            this.root = root;
            this.label = label;
        }

        public string Display { get { return root; } }
        public bool IsRemote { get { return false; } }
        public bool HidesDotFiles { get { return false; } }

        public string Full(string rel) { return rel.Length == 0 ? root : Path.Combine(root, rel); }

        public void CheckRoot()
        {
            if (string.IsNullOrEmpty(root)) throw new SyncException("Ordner " + label + " ist nicht angegeben.");
            if (Directory.Exists(root)) return;
            // Directory.Exists sagt nur "nein" – den eigentlichen Grund ermitteln (Rechner aus, Freigabe fehlt, kein Zugriff, Unterordner fehlt)
            string why = Diagnose(root);
            throw new SyncException("Ordner " + label + " nicht erreichbar: " + root + (why != null ? " \u2013 " + why : ""));
        }

        static string Diagnose(string path)
        {
            try
            {
                Directory.GetFileSystemEntries(path);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "kein Zugriff. Bei Freigaben unter „Anmeldung“ Benutzer und Kennwort hinterlegen.";
            }
            catch (DirectoryNotFoundException)
            {
                // tiefsten vorhandenen Oberordner suchen: "„Bilder“ fehlt in \\Rechner\Daten"
                try
                {
                    string p = path.TrimEnd('\\');
                    while (true)
                    {
                        string parent = Path.GetDirectoryName(p);
                        if (string.IsNullOrEmpty(parent)) break;
                        if (Directory.Exists(parent)) return "„" + Path.GetFileName(p) + "“ gibt es in " + parent + " nicht.";
                        p = parent.TrimEnd('\\');
                    }
                }
                catch { }
                return path.StartsWith("\\\\") ? "die Freigabe gibt es so nicht (Name prüfen, z. B. in der Netzwerkumgebung)." : "Laufwerk bzw. Ordner nicht vorhanden.";
            }
            catch (IOException ex)
            {
                // z. B. "Der Netzwerkpfad wurde nicht gefunden" (Rechner aus/nicht im Netz) oder
                // "Der Netzwerkname wurde nicht gefunden" (Freigabe gibt es nicht)
                return ex.Message.Trim().TrimEnd('.') + ".";
            }
            catch (Exception ex)
            {
                return ex.Message.Trim();
            }
        }

        // "unreadable" sammelt alles, was auf dieser Seite nicht sicher gesehen werden konnte
        // (nicht lesbar, versteckt, Verknuepfung). Solche Pfade werden nie als geloescht gewertet.
        public Dictionary<string, FileEntry> Scan(SyncJob job, Matcher m, Func<bool> cancel, Action<int> progress,
            HashSet<string> unreadable, List<string> errors)
        {
            Dictionary<string, FileEntry> result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push("");
            int count = 0;
            while (stack.Count > 0)
            {
                if (cancel()) throw new OperationCanceledException();
                string rel = stack.Pop();
                string full = Full(rel);
                FileSystemInfo[] infos;
                try { infos = new DirectoryInfo(full).GetFileSystemInfos(); }
                catch (Exception ex)
                {
                    if (rel.Length == 0) throw new SyncException("Ordner " + label + " nicht lesbar: " + root + " (" + ex.Message + ")");
                    unreadable.Add(rel);
                    errors.Add("Nicht lesbar: " + full + " (" + ex.Message + ")");
                    continue;
                }
                foreach (FileSystemInfo fi in infos)
                {
                    string r = rel.Length == 0 ? fi.Name : rel + "\\" + fi.Name;
                    FileAttributes at;
                    try { at = fi.Attributes; }
                    catch { unreadable.Add(r); continue; }
                    bool isDir = (at & FileAttributes.Directory) != 0;
                    if (m.Excluded(r, fi.Name, isDir)) continue;
                    if (!job.IncludeHidden && ((at & FileAttributes.Hidden) != 0 || (at & FileAttributes.System) != 0))
                    {
                        unreadable.Add(r);
                        continue;
                    }
                    if (isDir)
                    {
                        result[r] = new FileEntry { Rel = r, IsDir = true };
                        if ((at & FileAttributes.ReparsePoint) == 0) stack.Push(r);
                        else unreadable.Add(r);
                    }
                    else
                    {
                        FileInfo f = (FileInfo)fi;
                        try { result[r] = new FileEntry { Rel = r, Size = f.Length, Mtime = f.LastWriteTimeUtc }; }
                        catch (Exception ex) { errors.Add("Nicht lesbar: " + f.FullName + " (" + ex.Message + ")"); unreadable.Add(r); }
                    }
                    count++;
                    if ((count & 511) == 0) progress(count);
                }
            }
            progress(count);
            return result;
        }

        public Stream OpenRead(string rel)
        {
            return new FileStream(Full(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        }

        static void Unblock(string path)
        {
            FileAttributes at = File.GetAttributes(path);
            FileAttributes block = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
            if ((at & block) != 0) File.SetAttributes(path, at & ~block);
        }

        public FileEntry WriteFile(string rel, IFileSide from, FileEntry src)
        {
            string dst = Full(rel);
            string parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(dst)) Unblock(dst);
            LocalSide lf = from as LocalSide;
            if (lf != null)
            {
                File.Copy(lf.Full(rel), dst, true); // behält die Änderungszeit
            }
            else
            {
                // von FTP: erst in eine Hilfsdatei, damit bei Abbruch keine halbe Datei zurückbleibt
                string tmp = dst + ".syncpilot-tmp";
                try
                {
                    using (Stream i = from.OpenRead(rel))
                    using (FileStream o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        i.CopyTo(o, 1 << 20);
                        Sides.Finish(i);
                    }
                    if (File.Exists(dst)) File.Replace(tmp, dst, null);
                    else File.Move(tmp, dst);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                if (src.Mtime.Year >= 1980) File.SetLastWriteTimeUtc(dst, src.Mtime);
            }
            FileInfo fi = new FileInfo(dst);
            return new FileEntry { Rel = rel, Size = fi.Length, Mtime = fi.LastWriteTimeUtc };
        }

        public void CreateDir(string rel)
        {
            Directory.CreateDirectory(Full(rel));
        }

        public void Move(string fromRel, string toRel, bool isDir)
        {
            string from = Full(fromRel), to = Full(toRel);
            if (File.Exists(to) || Directory.Exists(to)) throw new IOException("Ziel ist schon vorhanden: " + toRel);
            string parent = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (isDir) Directory.Move(from, to);
            else File.Move(from, to);
        }

        bool IsNetwork(string path)
        {
            try
            {
                if (path.StartsWith("\\\\")) return true;
                string r = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(r) && new DriveInfo(r).DriveType == DriveType.Network;
            }
            catch { return false; }
        }

        string How(bool recycleWanted, string p, out bool recycle)
        {
            recycle = recycleWanted && !IsNetwork(p);
            return recycle ? "in den Papierkorb" : recycleWanted ? "endgültig, Netzlaufwerk ohne Papierkorb" : "endgültig";
        }

        public string DeleteFile(string rel, bool recycleWanted)
        {
            string p = Full(rel);
            if (!File.Exists(p)) return null;
            bool recycle;
            string how = How(recycleWanted, p, out recycle);
            if (recycle) Recycle.Send(p);
            else
            {
                Unblock(p);
                File.Delete(p);
            }
            return how;
        }

        public string DeleteDir(string rel, bool recycleWanted)
        {
            string p = Full(rel);
            if (!Directory.Exists(p) || Directory.EnumerateFileSystemEntries(p).Any()) return null;
            bool recycle;
            string how = How(recycleWanted, p, out recycle);
            if (recycle) Recycle.Send(p);
            else Directory.Delete(p);
            return how;
        }

        public void Dispose() { }
    }

    // =====================================================================

    public sealed class FtpSide : IFileSide
    {
        readonly FtpUrl url;
        readonly string label, display;
        readonly FtpClient client;
        readonly HashSet<string> madeDirs = new HashSet<string>(StringComparer.Ordinal);
        string basePath;
        bool connectFailed, hidesDot;

        public FtpSide(string address, string label, string user, string pass, string trustedThumbprint)
        {
            this.label = label;
            url = FtpUrl.Parse(address);
            display = address;
            client = new FtpClient(url, user, pass, trustedThumbprint);
        }

        public string Display { get { return display; } }
        public bool IsRemote { get { return true; } }
        public bool HidesDotFiles { get { return hidesDot; } }

        void Ensure()
        {
            if (client.Connected) return;
            if (connectFailed) throw new SyncException("Keine Verbindung zum FTP-Server (Ordner " + label + ").");
            try { client.Connect(); }
            catch
            {
                connectFailed = true;
                throw;
            }
            // Pfad in der Adresse gilt ab dem Startordner nach der Anmeldung; "//pfad" = absolut ab der Wurzel
            string p = url.Path ?? "";
            basePath = ResolveBase(p, client.Home, client.DirExists);
        }

        /// <summary>
        /// Startordner auf dem Server: Der Pfad in der Adresse gilt ab dem Startordner nach der Anmeldung, "//pfad" ab der Wurzel.
        /// Beginnt der Pfad schon mit dem Startordner („/home/benutzer/…“ bei Startordner /home/benutzer) oder gibt es ihn nur
        /// ab der Wurzel, gilt er ebenfalls ab der Wurzel – so wie Easy2Sync und viele FTP-Programme Pfade verstehen.
        /// </summary>
        internal static string ResolveBase(string p, string home, Func<string, bool> dirExists)
        {
            p = p ?? "";
            home = string.IsNullOrEmpty(home) ? "/" : home.TrimEnd('/');
            if (p.StartsWith("//")) return Root(p.Substring(1));
            if (home.Length > 0 && (p == home || p.StartsWith(home + "/"))) return Root(p);
            string rel = Root(home + "/" + p.TrimStart('/'));
            if (p.StartsWith("/") && p.Length > 1)
            {
                try { if (!dirExists(rel) && dirExists(Root(p))) return Root(p); }
                catch { }
            }
            return rel;
        }

        static string Root(string s)
        {
            s = (s ?? "").TrimEnd('/');
            return s.Length == 0 ? "/" : s;
        }

        string Full(string rel)
        {
            string b = basePath == "/" ? "" : basePath;
            return rel.Length == 0 ? (b.Length == 0 ? "/" : b) : b + "/" + rel.Replace('\\', '/');
        }

        static bool IsNetworkError(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return false;
            return ex is IOException || ex is SocketException || ex is ObjectDisposedException;
        }

        /// <summary>Führt einen Schritt aus; bei abgerissener Verbindung einmal neu verbinden und wiederholen.</summary>
        T Do<T>(Func<T> f)
        {
            Ensure();
            try { return f(); }
            catch (Exception ex)
            {
                if (!IsNetworkError(ex)) throw;
                client.Close();
                madeDirs.Clear();
                Ensure();
                return f();
            }
        }

        public void CheckRoot()
        {
            Do(delegate
            {
                if (!client.DirExists(Full("")))
                    throw new SyncException("Ordner " + label + " auf dem FTP-Server nicht gefunden: " + Full(""));
                return true;
            });
        }

        public Dictionary<string, FileEntry> Scan(SyncJob job, Matcher m, Func<bool> cancel, Action<int> progress,
            HashSet<string> unreadable, List<string> errors)
        {
            Dictionary<string, FileEntry> result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push("");
            int count = 0;
            while (stack.Count > 0)
            {
                if (cancel()) throw new OperationCanceledException();
                string rel = stack.Pop();
                List<FtpItem> items;
                bool incomplete = false, dot = false;
                try
                {
                    items = Do(delegate
                    {
                        bool inc, hd;
                        List<FtpItem> list = client.List(Full(rel), out inc, out hd);
                        incomplete = inc;
                        dot = hd;
                        return list;
                    });
                }
                catch (FtpException ex)
                {
                    if (rel.Length == 0) throw new SyncException("Ordner " + label + " auf dem FTP-Server nicht lesbar: " + ex.Message);
                    unreadable.Add(rel);
                    errors.Add("Nicht lesbar: " + Full(rel) + " (" + ex.Message + ")");
                    continue;
                }
                if (dot) hidesDot = true;
                if (incomplete)
                {
                    // Nicht jede Zeile der Liste war lesbar: in diesem Ordner nichts als gelöscht werten
                    unreadable.Add(rel);
                    errors.Add("Ordnerliste unvollst\u00e4ndig: " + Full(rel));
                }
                foreach (FtpItem fi in items)
                {
                    string r = rel.Length == 0 ? fi.Name : rel + "\\" + fi.Name;
                    if (m.Excluded(r, fi.Name, fi.IsDir)) continue;
                    if (fi.IsLink || fi.Name.IndexOf('\\') >= 0 || result.ContainsKey(r))
                    {
                        // Verknüpfung, Sonderzeichen oder zwei Namen, die sich nur in Groß-/Kleinschreibung unterscheiden
                        unreadable.Add(r);
                        continue;
                    }
                    if (fi.IsDir)
                    {
                        result[r] = new FileEntry { Rel = r, IsDir = true };
                        stack.Push(r);
                    }
                    else result[r] = new FileEntry { Rel = r, Size = fi.Size, Mtime = fi.Mtime, Precision = fi.Precision };
                    count++;
                    if ((count & 127) == 0) progress(count);
                }
            }
            progress(count);
            return result;
        }

        public Stream OpenRead(string rel)
        {
            return Do(delegate { return (Stream)client.OpenData("RETR " + Full(rel)); });
        }

        void MakeParents(string rel)
        {
            string[] parts = rel.Split('\\');
            string cur = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                cur = cur.Length == 0 ? parts[i] : cur + "\\" + parts[i];
                if (madeDirs.Contains(cur)) continue;
                string path = Full(cur);
                if (!client.DirExists(path)) client.MakeDir(path);
                madeDirs.Add(cur);
            }
        }

        /// <summary>Eintrag so, wie ihn auch die nächste Ordnerliste liefert (Größe, Zeit, Genauigkeit).</summary>
        FileEntry Stat(string rel)
        {
            int i = rel.LastIndexOf('\\');
            string parent = i < 0 ? "" : rel.Substring(0, i);
            string name = i < 0 ? rel : rel.Substring(i + 1);
            foreach (FtpItem it in client.List(Full(parent)))
                if (!it.IsDir && it.Name == name)
                    return new FileEntry { Rel = rel, Size = it.Size, Mtime = it.Mtime, Precision = it.Precision };
            return null;
        }

        public FileEntry WriteFile(string rel, IFileSide from, FileEntry src)
        {
            return Do(delegate
            {
                string full = Full(rel);
                string tmp = full + ".syncpilot-tmp";
                MakeParents(rel);
                try
                {
                    // erst unter Hilfsnamen hochladen, damit bei Abbruch keine halbe Datei unter dem echten Namen liegt
                    using (Stream i = from.OpenRead(rel))
                    using (FtpData o = client.OpenData("STOR " + tmp))
                    {
                        byte[] buf = new byte[1 << 16];
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                        Sides.Finish(i);
                        o.Finish();
                    }
                    try { client.DeleteFile(full); }
                    catch (FtpException) { } // gab es noch nicht
                    client.Rename(tmp, full);
                }
                catch
                {
                    try { if (client.Connected) client.DeleteFile(tmp); } catch { }
                    throw;
                }
                DateTime mtime = new DateTime(src.Mtime.Ticks - src.Mtime.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                if (src.Mtime.Year >= 1980) client.SetTime(full, mtime);
                // gespeicherter Stand = genau das, was der nächste Scan sieht
                FileEntry e = Stat(rel);
                if (e != null) return e;
                return new FileEntry { Rel = rel, Size = src.Size, Mtime = DateTime.UtcNow, Precision = 86400 };
            });
        }

        public void CreateDir(string rel)
        {
            Do(delegate
            {
                MakeParents(rel + "\\x");
                return true;
            });
        }

        public void Move(string fromRel, string toRel, bool isDir)
        {
            Do(delegate
            {
                // manche FTP-Server überschreiben ein vorhandenes Ziel beim Umbenennen: vorher prüfen
                if (client.DirExists(Full(toRel)) || Stat(toRel) != null) throw new IOException("Ziel ist schon vorhanden: " + toRel);
                MakeParents(toRel);
                client.Rename(Full(fromRel), Full(toRel));
                if (isDir) madeDirs.Clear(); // Pfade darunter haben sich geändert
                return true;
            });
        }

        public string DeleteFile(string rel, bool recycle)
        {
            return Do(delegate
            {
                try { client.DeleteFile(Full(rel)); }
                catch (FtpException ex)
                {
                    // 550 heißt "nicht vorhanden" oder "keine Berechtigung": nur bei wirklich fehlender Datei still übergehen
                    if (ex.Code == 550 && Stat(rel) == null) return null;
                    throw;
                }
                return "endg\u00fcltig, FTP ohne Papierkorb";
            });
        }

        public string DeleteDir(string rel, bool recycle)
        {
            return Do(delegate
            {
                string full = Full(rel);
                List<FtpItem> items;
                try { items = client.List(full); }
                catch (FtpException) { return (string)null; }
                if (items.Count > 0) return null;
                client.RemoveDir(full);
                madeDirs.Remove(rel);
                return "endg\u00fcltig, FTP ohne Papierkorb";
            });
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }

    // =====================================================================

    /// <summary>Ordner auf einem SFTP-Server (sftp://benutzer@server:22/pfad).</summary>
    public sealed class SftpSide : IFileSide
    {
        readonly FtpUrl url;
        readonly string label, display;
        readonly SftpClient client;
        readonly HashSet<string> madeDirs = new HashSet<string>(StringComparer.Ordinal);
        string basePath;
        bool connectFailed;

        public SftpSide(string address, string label, string user, string pass, string trustedFingerprint)
        {
            this.label = label;
            url = FtpUrl.Parse(address);
            display = address;
            client = new SftpClient(url, user, pass, trustedFingerprint);
        }

        public string Display { get { return display; } }
        public bool IsRemote { get { return true; } }
        public bool HidesDotFiles { get { return false; } }

        /// <summary>Ordner auf dem Server, wie er nach dem Verbinden aufgelöst wurde.</summary>
        public string RootPath { get { return Full(""); } }

        void Ensure()
        {
            if (client.Connected) return;
            if (connectFailed) throw new SyncException("Keine Verbindung zum SFTP-Server (Ordner " + label + ").");
            try { client.Connect(); }
            catch
            {
                connectFailed = true;
                throw;
            }
            // wie bei FTP: Pfad gilt ab dem Startordner nach der Anmeldung; "//pfad" = absolut ab der Wurzel
            string p = url.Path ?? "";
            basePath = FtpSide.ResolveBase(p, client.Home, client.DirExists);
        }

        string Full(string rel)
        {
            string b = basePath == "/" ? "" : basePath;
            return rel.Length == 0 ? (b.Length == 0 ? "/" : b) : b + "/" + rel.Replace('\\', '/');
        }

        static bool IsNetworkError(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return false;
            return ex is IOException || ex is SocketException || ex is ObjectDisposedException;
        }

        /// <summary>Führt einen Schritt aus; bei abgerissener Verbindung einmal neu verbinden und wiederholen.</summary>
        T Do<T>(Func<T> f)
        {
            Ensure();
            try { return f(); }
            catch (Exception ex)
            {
                if (!IsNetworkError(ex)) throw;
                client.Close();
                madeDirs.Clear();
                Ensure();
                return f();
            }
        }

        public void CheckRoot()
        {
            Do(delegate
            {
                if (!client.DirExists(Full("")))
                    throw new SyncException("Ordner " + label + " auf dem SFTP-Server nicht gefunden: " + Full("") +
                        " (der Pfad gilt ab dem Startordner " + client.Home + "; absolut mit // davor, z. B. sftp://server//var/www)");
                return true;
            });
        }

        public Dictionary<string, FileEntry> Scan(SyncJob job, Matcher m, Func<bool> cancel, Action<int> progress,
            HashSet<string> unreadable, List<string> errors)
        {
            Dictionary<string, FileEntry> result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push("");
            int count = 0;
            while (stack.Count > 0)
            {
                if (cancel()) throw new OperationCanceledException();
                string rel = stack.Pop();
                List<SftpItem> items;
                bool incomplete = false;
                try
                {
                    items = Do(delegate
                    {
                        bool inc;
                        List<SftpItem> list = client.List(Full(rel), out inc);
                        incomplete = inc;
                        return list;
                    });
                }
                catch (SftpException ex)
                {
                    if (rel.Length == 0) throw new SyncException("Ordner " + label + " auf dem SFTP-Server nicht lesbar: " + ex.Message);
                    unreadable.Add(rel);
                    errors.Add("Nicht lesbar: " + Full(rel) + " (" + ex.Message + ")");
                    continue;
                }
                if (incomplete)
                {
                    // Namen in fremder Zeichenkodierung: in diesem Ordner nichts als gelöscht werten
                    unreadable.Add(rel);
                    errors.Add("Ordnerliste unvollständig (Namen nicht lesbar): " + Full(rel));
                }
                foreach (SftpItem it in items)
                {
                    string r = rel.Length == 0 ? it.Name : rel + "\\" + it.Name;
                    bool isDir = it.Attrs.IsDir;
                    if (m.Excluded(r, it.Name, isDir)) continue;
                    bool special = !(isDir || (it.Attrs.IsFile && it.Attrs.HasSize && it.Attrs.HasTime));
                    if (special || it.Name.IndexOf('\\') >= 0 || result.ContainsKey(r))
                    {
                        // Verknüpfung, Gerätedatei, fehlende Angaben, Sonderzeichen oder Namen, die sich nur in Groß-/Kleinschreibung unterscheiden
                        unreadable.Add(r);
                        continue;
                    }
                    if (isDir)
                    {
                        result[r] = new FileEntry { Rel = r, IsDir = true };
                        stack.Push(r);
                    }
                    else result[r] = new FileEntry { Rel = r, Size = it.Attrs.Size, Mtime = it.Attrs.MtimeUtc, Precision = 0 };
                    count++;
                    if ((count & 127) == 0) progress(count);
                }
            }
            progress(count);
            return result;
        }

        public Stream OpenRead(string rel)
        {
            return Do(delegate { return client.OpenRead(Full(rel)); });
        }

        void MakeParents(string rel)
        {
            string[] parts = rel.Split('\\');
            string cur = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                cur = cur.Length == 0 ? parts[i] : cur + "\\" + parts[i];
                if (madeDirs.Contains(cur)) continue;
                string path = Full(cur);
                if (!client.DirExists(path)) client.MakeDir(path);
                madeDirs.Add(cur);
            }
        }

        FileEntry Stat(string rel)
        {
            SftpAttrs a = client.TryStat(Full(rel));
            if (a == null || !a.IsFile) return null;
            return new FileEntry { Rel = rel, Size = a.Size, Mtime = a.MtimeUtc, Precision = 0 };
        }

        public FileEntry WriteFile(string rel, IFileSide from, FileEntry src)
        {
            return Do(delegate
            {
                string full = Full(rel);
                string tmp = full + ".syncpilot-tmp";
                MakeParents(rel);
                try
                {
                    // erst unter Hilfsnamen hochladen, damit bei Abbruch keine halbe Datei unter dem echten Namen liegt
                    using (Stream i = from.OpenRead(rel))
                    {
                        client.Upload(tmp, i);
                        Sides.Finish(i);
                    }
                    client.Rename(tmp, full);
                }
                catch
                {
                    try { if (client.Connected) client.Remove(tmp); } catch { }
                    throw;
                }
                if (src.Mtime.Year >= 1980) client.SetTime(full, src.Mtime);
                FileEntry e = Stat(rel);
                if (e != null) return e;
                return new FileEntry { Rel = rel, Size = src.Size, Mtime = DateTime.UtcNow, Precision = 86400 };
            });
        }

        public void CreateDir(string rel)
        {
            Do(delegate
            {
                MakeParents(rel + "\\x");
                return true;
            });
        }

        public void Move(string fromRel, string toRel, bool isDir)
        {
            Do(delegate
            {
                MakeParents(toRel);
                client.Rename(Full(fromRel), Full(toRel));
                if (isDir) madeDirs.Clear(); // Pfade darunter haben sich geändert
                return true;
            });
        }

        public string DeleteFile(string rel, bool recycle)
        {
            return Do(delegate
            {
                try { client.Remove(Full(rel)); }
                catch (SftpException ex)
                {
                    if (ex.Code == SftpException.NoSuchFile) return null;
                    throw;
                }
                return "endgültig, SFTP ohne Papierkorb";
            });
        }

        public string DeleteDir(string rel, bool recycle)
        {
            return Do(delegate
            {
                string full = Full(rel);
                List<SftpItem> items;
                try { items = client.List(full); }
                catch (SftpException) { return (string)null; }
                if (items.Count > 0) return null;
                client.RemoveDir(full);
                madeDirs.Remove(rel);
                return "endgültig, SFTP ohne Papierkorb";
            });
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}