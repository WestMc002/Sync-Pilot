using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>Inhalt zweier Dateien vergleichen (Rechtsklick in der Analyse › „Inhalt vergleichen …“).</summary>
    public static class FileCompare
    {
        const long TextLimit = 5L * 1024 * 1024;

        /// <summary>Ergebnis als lesbarer Text; same = Inhalt identisch, text = als Text verglichen.</summary>
        public static string Describe(string pa, string pb, string na, string nb, out bool same, out bool text)
        {
            same = false;
            text = false;
            FileInfo fa = new FileInfo(pa), fb = new FileInfo(pb);
            if (!fa.Exists) return "Die Datei gibt es in " + na + " nicht mehr.";
            if (!fb.Exists) return "Die Datei gibt es in " + nb + " nicht mehr.";

            StringBuilder sb = new StringBuilder();
            long diffAt = FirstDifference(pa, pb);
            same = diffAt < 0;
            string times = na + ": " + Fmt.Size(fa.Length) + ", geändert " + fa.LastWriteTime.ToString("dd.MM.yyyy HH:mm:ss") + "\n" +
                           nb + ": " + Fmt.Size(fb.Length) + ", geändert " + fb.LastWriteTime.ToString("dd.MM.yyyy HH:mm:ss");
            if (same)
            {
                sb.Append("Der Inhalt ist identisch (Byte für Byte).");
                if (fa.LastWriteTimeUtc != fb.LastWriteTimeUtc) sb.Append(" Nur das Änderungsdatum unterscheidet sich.");
                sb.Append("\n\n").Append(times);
                return sb.ToString();
            }

            sb.Append("Der Inhalt ist unterschiedlich.\n\n").Append(times);
            if (fa.Length != fb.Length) sb.Append("\n\nUnterschied in der Größe: ").Append((fb.Length - fa.Length).ToString("+#,0;-#,0")).Append(" Bytes");
            sb.Append("\nErster Unterschied bei Byte ").Append(diffAt.ToString("N0"));

            if (fa.Length <= TextLimit && fb.Length <= TextLimit && LooksLikeText(pa) && LooksLikeText(pb))
            {
                text = true;
                string[] la = ReadLines(pa), lb = ReadLines(pb);
                int pre = 0;
                while (pre < la.Length && pre < lb.Length && la[pre] == lb[pre]) pre++;
                int suf = 0;
                while (suf < la.Length - pre && suf < lb.Length - pre && la[la.Length - 1 - suf] == lb[lb.Length - 1 - suf]) suf++;
                int endA = la.Length - suf, endB = lb.Length - suf;
                sb.Append("\n\nTextvergleich (").Append(la.Length.ToString("N0")).Append(" bzw. ").Append(lb.Length.ToString("N0")).Append(" Zeilen):\n");
                if (endA - pre == 0 && endB - pre == 0)
                    sb.Append("Nur Zeilenenden oder Kodierung unterscheiden sich.");
                else
                {
                    sb.Append("Abweichung ab Zeile ").Append(pre + 1).Append(" (").Append(Math.Max(endA, endB) - pre).Append(" Zeilen betroffen)\n");
                    AppendLines(sb, na, la, pre, endA);
                    AppendLines(sb, nb, lb, pre, endB);
                }
            }
            return sb.ToString().TrimEnd();
        }

        static void AppendLines(StringBuilder sb, string name, string[] lines, int from, int to)
        {
            sb.Append("\n").Append(name).Append(":");
            if (to <= from) { sb.Append(" (keine Zeilen)"); return; }
            int shown = 0;
            for (int i = from; i < to && shown < 3; i++, shown++)
            {
                string l = lines[i].Replace("\t", "  ");
                if (l.Length > 110) l = l.Substring(0, 110) + " …";
                sb.Append("\n  ").Append(i + 1).Append(": ").Append(l.Length == 0 ? "(leer)" : l);
            }
            if (to - from > shown) sb.Append("\n  … und ").Append(to - from - shown).Append(" weitere");
        }

        /// <summary>Position des ersten abweichenden Bytes, -1 wenn identisch.</summary>
        static long FirstDifference(string pa, string pb)
        {
            const int N = 1 << 20;
            byte[] ba = new byte[N], bb = new byte[N];
            using (FileStream a = new FileStream(pa, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, N))
            using (FileStream b = new FileStream(pb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, N))
            {
                long pos = 0;
                while (true)
                {
                    int ra = Fill(a, ba), rb = Fill(b, bb);
                    int n = Math.Min(ra, rb);
                    for (int i = 0; i < n; i++)
                        if (ba[i] != bb[i]) return pos + i;
                    if (ra != rb) return pos + n;
                    if (ra == 0) return -1;
                    pos += n;
                }
            }
        }

        static int Fill(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int r = s.Read(buf, total, buf.Length - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        static bool LooksLikeText(string p)
        {
            byte[] buf = new byte[8192];
            int n;
            using (FileStream f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = Fill(f, buf);
            // UTF-16 mit BOM gilt als Text, sonst: kein Nullbyte
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;
            for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }

        static string[] ReadLines(string p)
        {
            // UTF-8 erkennen, sonst Windows-Standardkodierung (ANSI)
            byte[] all = File.ReadAllBytes(p);
            string s;
            try { s = new UTF8Encoding(false, true).GetString(all); if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1); }
            catch (DecoderFallbackException)
            {
                using (StreamReader r = new StreamReader(new MemoryStream(all), Encoding.Default, true)) s = r.ReadToEnd();
            }
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>
        /// Vergleich im Hintergrund, Ergebnis als Meldung; bei Unterschieden und installiertem WinMerge dort weitervergleichen.
        /// (Analyse und Protokoll, Rechtsklick › „Inhalt vergleichen …“)
        /// </summary>
        public static void Show(Control owner, string pa, string pb, string na, string nb, string fileName)
        {
            owner.Cursor = Cursors.WaitCursor;
            Thread t = new Thread(delegate ()
            {
                string result;
                bool same = false, text = false;
                try { result = Describe(pa, pb, na, nb, out same, out text); }
                catch (Exception ex) { result = "Vergleich nicht möglich: " + ex.Message; }
                try
                {
                    owner.BeginInvoke((MethodInvoker)delegate
                    {
                        owner.Cursor = Cursors.Default;
                        string merge = WinMerge();
                        string head = fileName + "\n\n";
                        if (!same && merge != null)
                        {
                            if (MessageBox.Show(head + result + "\n\nIn WinMerge vergleichen?", "Inhalt vergleichen",
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(merge, "/e /u /dl \"" + na + "\" /dr \"" + nb + "\" \"" + pa + "\" \"" + pb + "\"");
                                }
                                catch (Exception ex) { MessageBox.Show("WinMerge ließ sich nicht starten: " + ex.Message, "Inhalt vergleichen", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                            }
                        }
                        else
                            MessageBox.Show(head + result, "Inhalt vergleichen", MessageBoxButtons.OK,
                                same ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Pfad zu WinMerge, falls installiert.</summary>
        public static string WinMerge()
        {
            List<string> c = new List<string>();
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Thingamahoochie\WinMerge"))
                    if (k != null && k.GetValue("Executable") is string) c.Add((string)k.GetValue("Executable"));
            }
            catch { }
            foreach (string pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                                          Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrEmpty(pf)) c.Add(Path.Combine(pf, @"WinMerge\WinMergeU.exe"));
            c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\WinMerge\WinMergeU.exe"));
            foreach (string p in c) if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }
    }
}
