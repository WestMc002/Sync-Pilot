// Sync Pilot – Screenshots mit Beispieldaten (hell und dunkel) für GitHub
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace SyncPilot
{
    /// <summary>Ein Lauf im Beispiel-Verlauf (Zeitpunkt relativ zu jetzt).</summary>
    public class DemoRun
    {
        public string Aufgabe;
        public int VorMinuten;
        public string Ergebnis;
        public int Kopiert, Geloescht, Konflikte, Fehler;
        public double Sekunden;
        public bool Geplant;
    }

    /// <summary>Ein Eintrag der Beispiel-Analyse. Größe -1 = Datei fehlt auf dieser Seite.</summary>
    public class DemoItem
    {
        public string Pfad;
        public ActionKind Aktion;
        public string Grund;
        public bool Ordner;
        public long GroesseA = -1, GroesseB = -1;
        public int AlterA, AlterB;
    }

    [XmlRoot("Beispieldaten")]
    public class DemoFile
    {
        [XmlArray("Aufgaben"), XmlArrayItem("Aufgabe")]
        public List<SyncJob> Jobs = new List<SyncJob>();
        [XmlArray("Verlauf"), XmlArrayItem("Lauf")]
        public List<DemoRun> Runs = new List<DemoRun>();
        public string AnalyseAufgabe;
        public int DateienA, DateienB;
        public long BytesA, BytesB;
        [XmlArray("Analyse"), XmlArrayItem("Eintrag")]
        public List<DemoItem> Items = new List<DemoItem>();
    }

    /// <summary>
    /// Erzeugt die Screenshots für die README aus Beispieldaten (Resources\Demo.xml, in die EXE eingebettet).
    /// Aufruf: "Sync Pilot.exe" --screenshots [Zielordner]. Echte Aufgaben, Verlauf, Status und Protokolle
    /// werden dabei weder gelesen noch geändert, es wird nichts synchronisiert.
    /// Ablage: hell.png / dunkel.png (Aufgabenübersicht) und &lt;name&gt;-hell.png / &lt;name&gt;-dunkel.png.
    /// Jedes Bild wird einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben sich
    /// durchsichtige runde Ecken und der Windows-Schatten (Verfahren wie Monitor Dimmer).
    /// </summary>
    internal static class Demo
    {
        /// <summary>true, solange die Screenshots erstellt werden (Beispieldaten statt echter Daten).</summary>
        public static bool Active;
        private static DemoFile _data;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // ---------- Beispieldaten ----------

        private static DemoFile Data
        {
            get
            {
                if (_data != null) return _data;
                using (Stream rs = typeof(Demo).Assembly.GetManifestResourceStream("Demo.xml"))
                {
                    if (rs == null) throw new FileNotFoundException("Demo.xml ist nicht in die EXE eingebettet.");
                    _data = (DemoFile)new XmlSerializer(typeof(DemoFile)).Deserialize(rs);
                }
                return _data;
            }
        }

        public static List<SyncJob> Jobs()
        {
            return Data.Jobs.Select(j => { SyncJob c = j.Clone(); c.Upgrade(); return c; }).ToList();
        }

        public static List<HistoryEntry> History()
        {
            DateTime now = DateTime.Now;
            List<HistoryEntry> list = new List<HistoryEntry>();
            foreach (DemoRun r in Data.Runs.OrderByDescending(x => x.VorMinuten))
            {
                SyncJob job = Data.Jobs.FirstOrDefault(j => j.Name == r.Aufgabe);
                HistoryEntry h = new HistoryEntry();
                h.Start = now.AddMinutes(-r.VorMinuten);
                h.JobId = job != null ? job.Id : "";
                h.JobName = r.Aufgabe;
                h.Result = r.Ergebnis;
                h.Copied = r.Kopiert;
                h.Deleted = r.Geloescht;
                h.Conflicts = r.Konflikte;
                h.Errors = r.Fehler;
                h.Seconds = r.Sekunden;
                h.Scheduled = r.Geplant;
                list.Add(h);
            }
            return list;
        }

        public static Analysis CreateAnalysis(SyncJob job)
        {
            DateTime now = DateTime.UtcNow;
            Analysis an = new Analysis();
            an.Job = job;
            an.FilesA = Data.DateienA;
            an.FilesB = Data.DateienB;
            an.BytesA = Data.BytesA;
            an.BytesB = Data.BytesB;
            an.Seconds = 2.4;
            foreach (DemoItem d in Data.Items)
            {
                PlanItem it = new PlanItem();
                it.Rel = d.Pfad;
                it.IsDir = d.Ordner;
                if (d.Ordner || d.GroesseA >= 0)
                    it.A = new FileEntry { Rel = d.Pfad, IsDir = d.Ordner, Size = Math.Max(0, d.GroesseA), Mtime = now.AddMinutes(-d.AlterA) };
                if (!d.Ordner && d.GroesseB >= 0)
                    it.B = new FileEntry { Rel = d.Pfad, Size = d.GroesseB, Mtime = now.AddMinutes(-d.AlterB) };
                if (it.A == null && it.B == null) continue;
                it.Proposed = d.Aktion;
                it.Chosen = d.Aktion;
                it.Reason = d.Grund ?? "";
                an.All.Add(it);
                an.Actions.Add(it);
            }
            return an;
        }

        /// <summary>Beispielprotokoll für den Screenshot der Protokollansicht (aus der Beispiel-Analyse).</summary>
        public static List<string> SampleLog(SyncJob job, HistoryEntry h)
        {
            if (job == null) job = Jobs().First();
            Analysis an = CreateAnalysis(job);
            DateTime t = h != null ? h.Start : DateTime.Now;
            List<string> lines = new List<string>();
            Action<string> add = delegate (string s) { lines.Add(t.ToString("HH:mm:ss") + "  " + s); t = t.AddSeconds(1); };
            add("Sync Pilot " + Runner.Version + " – Aufgabe: " + job.Name);
            add(job.SideName(true) + ": " + job.FolderA + "  (" + an.FilesA.ToString("N0") + " Dateien, " + Fmt.Size(an.BytesA) + ")");
            add(job.SideName(false) + ": " + job.FolderB + "  (" + an.FilesB.ToString("N0") + " Dateien, " + Fmt.Size(an.BytesB) + ")");
            add("Modus: " + job.ModeText + ", Löschen in den Papierkorb");
            add("Skript vor dem Kopieren: sichern.cmd");
            add("Skript:   3 Dateien gesichert");
            add("Skript beendet, Rückgabewert 0");
            int copied = 0, moved = 0, deleted = 0, conflicts = 0, skipped = 0, errors = 0;
            bool failed = false;
            foreach (PlanItem it in an.Actions)
            {
                switch (it.Chosen)
                {
                    case ActionKind.NachB:
                    case ActionKind.NachA:
                    {
                        string dir = job.Sides(it.Chosen == ActionKind.NachB ? "A \u2192 B" : "B \u2192 A");
                        if (!failed && !it.IsDir && it.Chosen == ActionKind.NachB)
                        {
                            failed = true;
                            errors++;
                            add("FEHLER bei " + it.Rel + ": Der Prozess kann nicht auf die Datei zugreifen, da sie von einem anderen Prozess verwendet wird.");
                            break;
                        }
                        copied++;
                        add((it.IsDir ? "Ordner angelegt (" : "Kopiert (") + dir + "): " + it.Rel);
                        break;
                    }
                    case ActionKind.LoeschenA:
                    case ActionKind.LoeschenB:
                        deleted++;
                        add((it.IsDir ? "Ordner gelöscht (" : "Gelöscht (") + job.SideName(it.Chosen == ActionKind.LoeschenA) + ", Papierkorb): " + it.Rel);
                        break;
                    case ActionKind.UmbenennenA:
                    case ActionKind.UmbenennenB:
                        moved++;
                        add("Umbenannt/verschoben (" + job.SideName(it.Chosen == ActionKind.UmbenennenA) + "): " + (it.From ?? it.Rel) + " \u2192 " + it.Rel);
                        break;
                    case ActionKind.Konflikt:
                        conflicts++;
                        add("Konflikt, übersprungen (" + job.Sides(it.Reason) + "): " + it.Rel);
                        break;
                    default:
                        skipped++;
                        add("Übersprungen: " + it.Rel);
                        break;
                }
            }
            add("Ergebnis: " + copied + " kopiert, " + (moved > 0 ? moved + " umbenannt/verschoben, " : "") + deleted + " gelöscht, " +
                conflicts + " Konflikte, " + skipped + " übersprungen, " + errors + " Fehler");
            return lines;
        }

        // ---------- Screenshots ----------

        public static bool RunScreenshots(string outDir)
        {
            try
            {
                Active = true;
                Directory.CreateDirectory(outDir);
                try { File.Delete(Path.Combine(outDir, "Fehler.txt")); } catch { }
                DemoFile data = Data;
                SyncJob analyseJob = data.Jobs.FirstOrDefault(j => j.Name == data.AnalyseAufgabe) ?? data.Jobs.First();

                // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm)
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);

                using (Backdrop backdrop = new Backdrop())
                {
                    backdrop.Cover(new Rectangle(area.Right - 2, area.Bottom - 2, 1, 1), Color.Black, IntPtr.Zero);
                    Pump(300);
                    backdrop.Hide();

                    foreach (bool dark in new bool[] { false, true })
                    {
                        Theme.Override = dark;
                        Theme.Init();
                        string suffix = dark ? "dunkel" : "hell";

                        Shot(backdrop, delegate { return Window(null); }, Path.Combine(outDir, suffix + ".png"));
                        Shot(backdrop, delegate { return Window(delegate (MainForm f) { f.ShowAnalysis(FindJob(f, analyseJob), false); }); },
                            Path.Combine(outDir, "analyse-" + suffix + ".png"));
                        Shot(backdrop, delegate { return Window(delegate (MainForm f) { f.ShowEdit(FindJob(f, analyseJob)); }); },
                            Path.Combine(outDir, "aufgabe-" + suffix + ".png"));
                        Shot(backdrop, delegate { return Window(delegate (MainForm f) { f.Navigate(new HistoryView(f)); }); },
                            Path.Combine(outDir, "verlauf-" + suffix + ".png"));
                        Shot(backdrop, delegate { return Window(delegate (MainForm f) { f.ShowLog(SampleEntry(f, analyseJob), true); }); },
                            Path.Combine(outDir, "protokoll-" + suffix + ".png"));
                        Shot(backdrop, delegate { return AboutDialog.CreateForScreenshot(); }, Path.Combine(outDir, "info-" + suffix + ".png"));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(outDir, "Fehler.txt"), ex.ToString()); } catch { }
                return false;
            }
            finally
            {
                Theme.Override = null;
                Active = false;
            }
        }

        /// <summary>Lauf der Analyse-Aufgabe aus dem Beispielverlauf (oder ein neuer) für den Protokoll-Screenshot.</summary>
        private static HistoryEntry SampleEntry(MainForm f, SyncJob job)
        {
            SyncJob j = FindJob(f, job);
            HistoryEntry h = History().FirstOrDefault(x => x.JobId == j.Id);
            if (h == null)
            {
                h = new HistoryEntry();
                h.Start = DateTime.Now.AddMinutes(-12);
                h.JobId = j.Id;
                h.JobName = j.Name;
                h.Seconds = 14;
            }
            // passend zum Beispielprotokoll: ein Fehler
            h.Errors = 1;
            h.Result = "1 Fehler";
            h.Cancelled = false;
            return h;
        }

        private static SyncJob FindJob(MainForm f, SyncJob job)
        {
            return f.Jobs.FirstOrDefault(j => j.Id == job.Id) ?? f.Jobs.First();
        }

        /// <summary>Hauptfenster in fester Größe, optional gleich mit einer anderen Ansicht.</summary>
        private static Form Window(Action<MainForm> setup)
        {
            MainForm f = new MainForm();
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Size = new Size(Theme.P(1200), Theme.P(800));
            if (setup != null) setup(f);
            return f;
        }

        private static void Shot(Backdrop backdrop, Func<Form> create, string file)
        {
            using (Form f = create())
            {
                f.ShowInTaskbar = false;
                f.Show();
                f.TopMost = true;
                f.Activate();
                Pump(1500); // Aufbau, Symbole und Listen laden
                RECT r;
                Rectangle bounds = DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                    ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                    : f.Bounds;
                Save(Capture(backdrop, f.Handle, bounds, Margin(f)), file);
                f.Close();
            }
            Pump(300);
        }

        /// <summary>Rand um das Fenster für den Schatten (24 Einheiten).</summary>
        private static int Margin(Control c)
        {
            try { using (Graphics g = c.CreateGraphics()) return (int)Math.Round(24 * g.DpiX / 96f); }
            catch { return 24; }
        }

        /// <summary>Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit.</summary>
        private static Bitmap Capture(Backdrop backdrop, IntPtr target, Rectangle bounds, int margin)
        {
            Rectangle r = Rectangle.Inflate(bounds, margin, margin);
            backdrop.Cover(r, Color.Black, target);
            Pump(300);
            using (Bitmap onBlack = Grab(r))
            {
                backdrop.Cover(r, Color.White, target);
                Pump(300);
                using (Bitmap onWhite = Grab(r))
                {
                    backdrop.Hide();
                    return Combine(onBlack, onWhite);
                }
            }
        }

        private static Bitmap Grab(Rectangle r)
        {
            Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        private static Bitmap Combine(Bitmap onBlack, Bitmap onWhite)
        {
            int w = onBlack.Width, h = onBlack.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData wd = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData od = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = bd.Stride * h;
                byte[] b = new byte[n];
                byte[] wh = new byte[n];
                byte[] o = new byte[n];
                Marshal.Copy(bd.Scan0, b, 0, n);
                Marshal.Copy(wd.Scan0, wh, 0, n);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    int diff = ((wh[i] - b[i]) + (wh[i + 1] - b[i + 1]) + (wh[i + 2] - b[i + 2])) / 3;
                    int a = Math.Max(0, Math.Min(255, 255 - diff));
                    if (a == 0) continue; // ganz durchsichtig
                    for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, b[i + c] * 255 / a);
                    o[i + 3] = (byte)a;
                }
                Marshal.Copy(o, 0, od.Scan0, n);
            }
            finally
            {
                onBlack.UnlockBits(bd);
                onWhite.UnlockBits(wd);
                result.UnlockBits(od);
            }
            return result;
        }

        private static void Save(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }

        /// <summary>Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden).</summary>
        private static void Pump(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Einfarbige Fläche hinter dem aufgenommenen Bereich.</summary>
        private sealed class Backdrop : Form
        {
            public Backdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void Cover(Rectangle r, Color color, IntPtr target)
            {
                BackColor = color;
                Bounds = r;
                if (!Visible) Show();
                // vor alle anderen Fenster legen, danach das aufgenommene Fenster wieder davor
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                if (target != IntPtr.Zero) SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                Refresh();
            }

            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}