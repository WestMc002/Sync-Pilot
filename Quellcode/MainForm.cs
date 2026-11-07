// Sync Pilot – Hauptfenster mit Navigation, Footer und Info-Fenster
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SyncPilot
{
    public class BaseView : UserControl
    {
        protected readonly MainForm Main;

        public BaseView(MainForm main)
        {
            Main = main;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            DoubleBuffered = true;
        }

        public virtual bool Busy { get { return false; } }
        public virtual void RequestCancel() { }
        public virtual void ApplySearch(string text) { }
        public virtual string NavKey { get { return ""; } }
    }

    public class MainForm : Form
    {
        public List<SyncJob> Jobs;
        readonly Panel header, nav, navSpacer, host;
        readonly FlatButton navMenu, navPin;
        readonly System.Windows.Forms.Timer navTimer;
        bool navOpen;
        readonly SignatureFooter footer;
        readonly FlatButton navTasks, navHistory, navSettings, navQuit;
        NotifyIcon tray;
        bool startHidden, exitRequested, trayHintShown;
        readonly Image appIcon;
        Rectangle pill;
        bool pillHover;
        // Update-Hinweis oben im Inhalt (nur sichtbar, wenn auf GitHub eine neuere Version liegt)
        Panel updateHost;
        UpdateBar updateBar;
        ReleaseInfo available;
        bool updating;
        /// <summary>Gesetzt beim Neustart nach einem Update (--nach-update).</summary>
        public static bool AfterUpdate;
        readonly InputBox search;
        BaseView current;
        bool closeAfterCancel;

        public MainForm() : this(false) { }

        /// <summary>startHidden: nur im Infobereich starten (Autostart mit /tray).</summary>
        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            Text = "Sync Pilot";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = Theme.Body;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            // Startgroesse: 1440 x 940, hoechstens 90 % des Arbeitsbereichs des Bildschirms
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Theme.P(1440), work.Width * 9 / 10), Math.Min(Theme.P(940), work.Height * 9 / 10));
            MinimumSize = new Size(Theme.P(880), Theme.P(580));
            KeyPreview = true;

            try { Jobs = Store.LoadJobs(); }
            catch (Exception ex)
            {
                MessageBox.Show("Die Aufgaben konnten nicht geladen werden:\n" + ex.Message, "Sync Pilot",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Jobs = new List<SyncJob>();
            }
            // Programm verschoben? Zeitpläne auf den neuen Ort umstellen
            if (Scheduler.RepairAfterMove(Jobs)) SaveJobs();
            // ältere Zeitpläne (Täglich, Stündlich) auf die neue Aufgabenplanung umstellen
            if (!Demo.Active && Jobs.Exists(j => j.NeedsReapply))
            {
                foreach (SyncJob j in Jobs) if (j.NeedsReapply) Scheduler.Apply(j);
                SaveJobs();
            }

            // Kopfzeile
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.P(108); // wie in den anderen Programmen (Vorlage: Wake on LAN, HeaderPanel)
            header.BackColor = Theme.Surface2;
            header.Paint += PaintHeader;
            appIcon = AppImages.AppIcon(64);
            header.MouseMove += delegate (object s, MouseEventArgs e)
            {
                bool h = pill.Contains(e.Location);
                if (h != pillHover) { pillHover = h; header.Cursor = h ? Cursors.Hand : Cursors.Default; header.Invalidate(); }
            };
            header.MouseLeave += delegate
            {
                if (pillHover) { pillHover = false; header.Cursor = Cursors.Default; header.Invalidate(); }
            };
            header.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && pill.Contains(e.Location)) ShowInfo();
            };
            search = new InputBox(false, false);
            search.BackColor = Theme.Surface2;
            search.Width = Theme.P(300);
            search.Box.AccessibleName = "Aufgaben suchen";
            search.Cue = "Aufgaben suchen \u2026";
            search.Box.TextChanged += delegate { if (current != null) current.ApplySearch(search.Text); };
            header.Controls.Add(search);
            header.Resize += delegate
            {
                int w = Math.Min(Theme.P(300), Math.Max(Theme.P(160), header.Width / 3));
                search.SetBounds(header.Width - w - Theme.P(28), Theme.P(32), w, search.Height);
            };

            // Navigation
            // Navigation ein- und ausklappbar: angepinnt = immer offen und schiebt den Inhalt; sonst nur Symbole,
            // ☰ klappt sie über dem Inhalt aus, sie klappt nach der Auswahl bzw. beim Verlassen wieder ein
            navSpacer = new Panel();
            navSpacer.Dock = DockStyle.Left;
            navSpacer.BackColor = Theme.Bg;
            nav = new Panel();
            nav.Width = Theme.P(230);
            nav.BackColor = Theme.Bg;
            nav.Paint += delegate (object s, PaintEventArgs e)
            {
                if (navOpen && !AppSettings.Current.NavPinned)
                    using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, nav.Width - 1, 0, nav.Width - 1, nav.Height);
            };
            navMenu = NavButton("", "\uE700");
            navMenu.AccessibleName = "Navigation aus- bzw. einklappen";
            navMenu.Click += delegate
            {
                if (AppSettings.Current.NavPinned) SetNavPinned(false);
                else { navOpen = !navOpen; ApplyNav(); }
            };
            navPin = new FlatButton("", "\uE718", BtnKind.Subtle);
            navPin.GlyphSize = 12f;
            navPin.Click += delegate { SetNavPinned(!AppSettings.Current.NavPinned); };
            nav.Controls.Add(navPin);
            navTimer = new System.Windows.Forms.Timer();
            navTimer.Interval = 400;
            navTimer.Tick += delegate
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt
                if (!navOpen || AppSettings.Current.NavPinned || nav.Capture) return;
                if (!nav.RectangleToScreen(nav.ClientRectangle).Contains(Cursor.Position)) { navOpen = false; ApplyNav(); }
            };
            navTimer.Start();
            navTasks = NavButton("Aufgaben", Theme.GSwitch);
            navHistory = NavButton("Verlauf", Theme.GHistory);
            navSettings = NavButton("Einstellungen", Theme.GSettings);
            navSettings.Click += delegate { Navigate(new SettingsView(this)); };
            // Komplett beenden (sonst schließt das X nur das Fenster und Sync Pilot läuft im Infobereich weiter)
            navQuit = NavButton("Beenden", "\uE7E8");
            navQuit.AccessibleName = "Sync Pilot beenden (Strg+Q)";
            navQuit.Click += delegate { ExitApp(); };
            navTasks.Click += delegate { ShowTasks(); };
            navHistory.Click += delegate { Navigate(new HistoryView(this)); };
            nav.Resize += delegate { LayoutNav(); };
            foreach (FlatButton b in new FlatButton[] { navTasks, navHistory, navSettings, navQuit })
                b.Click += delegate { if (!AppSettings.Current.NavPinned && navOpen) { navOpen = false; ApplyNav(); } };

            host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.Bg;

            // Update-Hinweis: oben im Inhalt, bis eine neuere Version gefunden wird ausgeblendet
            updateBar = new UpdateBar();
            updateBar.Dock = DockStyle.Fill;
            updateBar.Install.Click += async delegate { await InstallUpdate(); };
            updateBar.Page.Click += delegate { OpenReleasePage(); };
            updateBar.Later.Click += delegate { ShowUpdateBar(false); };
            updateHost = new Panel();
            updateHost.Dock = DockStyle.Top;
            updateHost.BackColor = Theme.Bg;
            updateHost.Padding = new Padding(Theme.P(32), Theme.P(16), Theme.P(32), 0);
            updateHost.Height = Theme.P(16) + updateBar.Height;
            updateHost.Visible = false;
            updateHost.Controls.Add(updateBar);
            host.Controls.Add(updateHost);

            // kurze Meldung oben im Inhalt (z. B. nach dem Speichern der Einstellungen), verschwindet nach einigen Sekunden
            noticeHost = new Panel();
            noticeHost.Dock = DockStyle.Top;
            noticeHost.BackColor = Theme.Bg;
            noticeHost.Padding = new Padding(Theme.P(32), Theme.P(12), Theme.P(32), 0);
            noticeHost.Height = Theme.P(12 + 40);
            noticeHost.Visible = false;
            notice = new Label();
            notice.Dock = DockStyle.Fill;
            notice.AutoEllipsis = true;
            notice.TextAlign = ContentAlignment.MiddleLeft;
            notice.Padding = new Padding(Theme.P(12), 0, Theme.P(12), 0);
            notice.Font = Theme.Body;
            notice.Click += delegate { noticeHost.Visible = false; };
            noticeHost.Controls.Add(notice);
            host.Controls.Add(noticeHost);
            noticeTimer = new System.Windows.Forms.Timer();
            noticeTimer.Interval = 8000;
            noticeTimer.Tick += delegate { noticeTimer.Stop(); noticeHost.Visible = false; };

            footer = new SignatureFooter();

            // Andockreihenfolge: Kopf und Footer ueber die volle Breite, dann Navigation, dann Inhalt
            Controls.Add(host);
            Controls.Add(navSpacer);
            Controls.Add(footer);
            Controls.Add(header);
            host.BringToFront();
            Controls.Add(nav); // nicht angedockt: liegt ausgeklappt über dem Inhalt
            nav.BringToFront();
            Resize += delegate { ApplyNav(); };
            footer.LocationChanged += delegate { ApplyNav(); };
            header.SizeChanged += delegate { ApplyNav(); };
            ApplyNav();

            ShowTasks();
            if (!Demo.Active)
            {
                SetupTray();
                Triggers.Finished += TriggerFinished;
                Triggers.Apply(Jobs);
                // Update-Prüfung still kurz nach dem Start (höchstens einmal täglich, abschaltbar im Info-Fenster),
                // auch wenn Sync Pilot nur im Infobereich startet
                System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer();
                updTimer.Interval = 15000;
                updTimer.Tick += async delegate { updTimer.Stop(); updTimer.Dispose(); await CheckForUpdates(false); };
                updTimer.Start();
                // läuft oft tagelang im Infobereich: alle 6 Stunden nachsehen, ob die Prüfung fällig ist (höchstens alle 20 Stunden)
                System.Windows.Forms.Timer updRepeat = new System.Windows.Forms.Timer();
                updRepeat.Interval = 6 * 60 * 60 * 1000;
                updRepeat.Tick += async delegate { await CheckForUpdates(false); };
                updRepeat.Start();

            }
        }

        // ---------- Infobereich (Hintergrundbetrieb) ----------

        void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "Sync Pilot";
            ModernMenuBuilder mb = new ModernMenuBuilder(this);
            ContextMenuStrip m = mb.CreateMenu();
            trayMenu = m;
            trayBuilder = mb;
            m.Items.Add(mb.Item("Sync Pilot \u00f6ffnen", MenuHelper.Sync, delegate { ShowFromTray(); }));
            m.Items.Add(mb.Item("Einstellungen", MenuHelper.Settings, delegate { ShowFromTray(); Navigate(new SettingsView(this)); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(mb.Item("Beenden", '\uE711', delegate { ExitApp(); }));
            tray.ContextMenuStrip = m;
            tray.MouseClick += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            // Klick auf den Update-Hinweis startet das Update (Globale Richtlinien, Updates)
            tray.BalloonTipClicked += async delegate
            {
                if (!balloonIsUpdate || available == null) return;
                ShowFromTray();
                await InstallUpdate();
            };
            UpdateTray();

            // Ein zweiter Start von Sync Pilot holt dieses Fenster nach vorn bzw. übergibt Ordner aus dem Explorer
            if (!IsHandleCreated) CreateHandle();
            Listen("Local\\SyncPilot_Show", delegate { ShowFromTray(); CheckExplorer(); });
            Listen(ExplorerMenu.EventName, CheckExplorer);
            Listen(Shortcuts.EventName, delegate { ShowFromTray(); CheckPendingSync(); });
        }

        void Listen(string eventName, Action action)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    using (EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, eventName))
                        while (true)
                        {
                            ev.WaitOne();
                            if (IsDisposed) return;
                            try { BeginInvoke(action); } catch { return; }
                        }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        bool explorerOpen;

        /// <summary>Im Explorer gewähltes Ordnerpaar übernehmen: Richtung wählen, Aufgabe anlegen, analysieren.</summary>
        public void CheckExplorer()
        {
            if (explorerOpen || Demo.Active) return;
            string a, b;
            if (!ExplorerMenu.TakePair(out a, out b)) return;
            explorerOpen = true;
            try
            {
                ShowFromTray();
                if (current != null && current.Busy)
                {
                    MessageBox.Show(this, "Es l\u00e4uft gerade noch ein Vorgang. Bitte die beiden Ordner danach erneut im Explorer w\u00e4hlen.",
                        "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                foreach (SyncJob j in Jobs)
                {
                    string ja = Paths.TryResolve(j.FolderA), jb = Paths.TryResolve(j.FolderB);
                    if (!(ExplorerMenu.Same(ja, a) && ExplorerMenu.Same(jb, b)) && !(ExplorerMenu.Same(ja, b) && ExplorerMenu.Same(jb, a))) continue;
                    DialogResult r = MessageBox.Show(this, "F\u00fcr diese beiden Ordner gibt es schon die Aufgabe \u201e" + j.Name + "\u201c.\n\n" +
                        "Ja = diese Aufgabe jetzt analysieren\nNein = trotzdem eine neue Aufgabe anlegen",
                        "Sync Pilot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) { ShowAnalysis(j, false); return; }
                    if (r != DialogResult.No) return;
                    break;
                }
                using (PairDialog d = new PairDialog(a, b))
                {
                    DialogResult r = d.ShowDialog(this);
                    if (r != DialogResult.Yes && r != DialogResult.No) return;
                    SyncJob job = d.CreateJob(Jobs);
                    if (r == DialogResult.No) { Navigate(new EditView(this, job, true)); return; }
                    Jobs.Add(job);
                    SaveJobs();
                    ShowAnalysis(job, false);
                }
            }
            finally { explorerOpen = false; }
        }

        /// <summary>Verknüpfung "/sync": Aufgabe analysieren und gleich synchronisieren.</summary>
        public void CheckPendingSync()
        {
            if (Demo.Active) return;
            string id = Shortcuts.TakePending();
            if (id == null) return;
            SyncJob job = Jobs.Find(j => j.Id == id) ?? Jobs.Find(j => string.Equals(j.Name, id, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                MessageBox.Show(this, "Die Aufgabe dieser Verknüpfung gibt es nicht mehr.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (job.Disabled)
            {
                MessageBox.Show(this, "Die Aufgabe \u201e" + job.Name + "\u201c ist deaktiviert.", "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowAnalysis(job, true);
        }

        /// <summary>Symbol im Infobereich zeigen, solange Hintergrundbetrieb an ist oder das Fenster versteckt ist.</summary>
        public void UpdateTray()
        {
            // "Beenden" nur nötig, wenn das X das Programm nicht beendet
            if (navQuit != null)
            {
                navQuit.Visible = AppSettings.Current.Background;
                LayoutNav();
            }
            if (tray == null) return;
            tray.Visible = AppSettings.Current.Background || !Visible;
        }

        public void ShowFromTray()
        {
            startHidden = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            UpdateTray();
        }

        void ExitApp()
        {
            if (RemoteRun.SomeBusy &&
                MessageBox.Show("Am Handy wurde ein Abgleich gestartet, der noch l\u00e4uft. Trotzdem beenden?", "Sync Pilot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            if (Triggers.SomeBusy &&
                MessageBox.Show("Ein automatisch gestarteter Abgleich l\u00e4uft noch. Abbrechen und beenden?", "Sync Pilot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            exitRequested = true;
            if (!Visible) Show();
            Close();
            if (!IsDisposed && Visible && !closeAfterCancel) exitRequested = false; // Beenden wurde abgebrochen
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            base.OnFormClosed(e);
        }

        FlatButton NavButton(string text, string glyph)
        {
            FlatButton b = new FlatButton(text, glyph, BtnKind.Nav);
            if (text.Length > 0) b.AccessibleName = text;
            nav.Controls.Add(b);
            return b;
        }

        void SetNavPinned(bool pinned)
        {
            AppSettings.Current.NavPinned = pinned;
            AppSettings.Current.Save();
            navOpen = pinned;
            ApplyNav();
        }

        void ApplyNav()
        {
            if (navPin == null || header == null || footer == null) return;
            bool pinned = AppSettings.Current.NavPinned;
            bool open = pinned || navOpen;
            int full = Theme.P(230), compact = Theme.P(68);
            navSpacer.Width = pinned ? full : compact;
            int top = header.Bottom, h = Math.Max(0, footer.Top - header.Bottom);
            nav.SetBounds(0, top, open ? full : compact, h);
            nav.BringToFront();
            navPin.Visible = open;
            navPin.Glyph = pinned ? "\uE77A" : "\uE718";
            navPin.AccessibleName = pinned ? "Navigation lösen (einklappbar)" : "Navigation anpinnen";
            nav.Invalidate();
            LayoutNav();
        }

        void LayoutNav()
        {
            int x = Theme.P(10), w = nav.Width - Theme.P(20), h = navTasks.Height;
            int y = Theme.P(14);
            if (navMenu != null)
            {
                navMenu.SetBounds(x, y, Theme.P(48), h);
                navPin.SetBounds(nav.Width - Theme.P(10) - Theme.P(36), y + (h - Theme.P(36)) / 2, Theme.P(36), Theme.P(36));
                y += h + Theme.P(10);
            }
            navTasks.SetBounds(x, y, w, h);
            navHistory.SetBounds(x, y + h + Theme.P(4), w, h);
            int bottom = nav.Height - Theme.P(14);
            if (navQuit != null && AppSettings.Current.Background)
            {
                navQuit.SetBounds(x, bottom - h, w, h);
                bottom -= h + Theme.P(4);
            }
            navSettings.SetBounds(x, bottom - h, w, h);
        }

        // Kopf: Programm-Icon, Name, Versions-Plakette (Klick oeffnet das Info-Fenster)
        /// <summary>Kurze Erklärung unter dem Programmnamen (wie in den anderen Programmen).</summary>
        public const string Subtitle = "Ordner abgleichen, sichern und spiegeln";

        // Einheitlicher Kopf (Globale Richtlinien, Vorlage Wake on LAN): Icon links, daneben Name und Erklärung als Block
        // mittig zum Icon; Versions-Pille 12 × Skalierung rechts neben dem Namen (wie Wake on LAN), senkrecht mittig zum Namen
        void PaintHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.Scale;
            int x = (int)(28 * k), iconTop = (int)(28 * k), icon = (int)(52 * k);
            if (appIcon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(appIcon, x, iconTop, icon, icon);
            }
            int tx = x + icon + (int)(18 * k);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            using (Font fTitle = new Font("Segoe UI Semibold", 17f))
            using (Font fSub = new Font("Segoe UI", 10f))
            using (Font fPill = new Font("Segoe UI", 10.5f * k, GraphicsUnit.Pixel))
            {
                Size ts = TextRenderer.MeasureText(g, "Sync Pilot", fTitle, Size.Empty, flags);
                Size ss = TextRenderer.MeasureText(g, Subtitle, fSub, Size.Empty, flags);
                int gap = (int)(4 * k);
                int top = iconTop + (icon - ts.Height - gap - ss.Height) / 2;
                TextRenderer.DrawText(g, "Sync Pilot", fTitle, new Point(tx, top), Theme.Text, flags);
                TextRenderer.DrawText(g, Subtitle, fSub, new Point(tx + (int)(1 * k), top + ts.Height + gap), Theme.TextDim, flags);

                // Versions-Plakette nach globaler Vorgabe (Referenz Wake on LAN ab 1.42, HeaderPanel): 12 neben dem Namen
                // (Textende, NoPadding), Höhe 21, Schrift 10,5 px, Innenabstand je 8, senkrecht mittig zum Namen;
                // Farben wie die Referenz (14 % bzw. 38 % Akzentfarbe); Klick öffnet das Info-Fenster
                string v = "Version " + AppInfo.Version;
                Size vs = TextRenderer.MeasureText(g, v, fPill, Size.Empty, flags);
                int ph = (int)Math.Round(21 * k), pw = vs.Width + (int)Math.Round(16 * k);
                Rectangle r = new Rectangle(tx + ts.Width + (int)Math.Round(12 * k), top + (ts.Height - ph) / 2, pw, ph);
                pill = r;
                Color fill = AccentColors.Mix(Theme.Accent, Theme.Bg, 0.14), border = AccentColors.Mix(Theme.Accent, Theme.Bg, 0.38);
                using (GraphicsPath p = Shapes.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), (r.Height - 1) / 2f))
                {
                    using (SolidBrush b = new SolidBrush(pillHover ? border : fill)) g.FillPath(b, p);
                    using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, v, fPill, r, Theme.AccentSoftText, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BeginInvoke(new Action(CheckExplorer));
            BeginInvoke(new Action(CheckPendingSync));
            BeginInvoke(new Action(ShowUpdateResult));
            LayoutNav();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                search.Box.Focus();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Q))
            {
                ExitApp();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- Navigation ----------

        public bool Navigate(BaseView v)
        {
            if (current != null && current.Busy)
            {
                MessageBox.Show("Es läuft gerade noch ein Vorgang. Bitte warten oder zuerst abbrechen.",
                    "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                v.Dispose();
                return false;
            }
            host.SuspendLayout();
            BaseView old = current;
            current = v;
            v.Dock = DockStyle.Fill;
            host.Controls.Add(v);
            if (noticeHost != null) noticeHost.SendToBack();
            updateHost.SendToBack(); // Update-Hinweis zuerst andocken (oben), die Seite füllt den Rest
            if (old != null)
            {
                host.Controls.Remove(old);
                old.Dispose();
            }
            host.ResumeLayout();
            navTasks.Selected = v.NavKey == "tasks";
            navHistory.Selected = v.NavKey == "history";
            navSettings.Selected = v.NavKey == "settings";
            search.Visible = v.NavKey == "tasks";
            if (search.Visible && search.Text.Length > 0) v.ApplySearch(search.Text);
            v.Focus();
            return true;
        }

        public void ShowTasks() { Navigate(new TasksView(this)); }
        public void ShowAnalysis(SyncJob job, bool autoSync) { Navigate(new AnalysisView(this, job, autoSync)); }
        public void ShowEdit(SyncJob job) { Navigate(new EditView(this, job)); }

        // ---------- Updates (GitHub-Releases) ----------

        public void ShowInfo()
        {
            UpdateHooks hooks = new UpdateHooks();
            hooks.Status = UpdateState;
            hooks.Auto = delegate { return AppSettings.Current.UpdateCheck; };
            hooks.SetAuto = delegate (bool on)
            {
                if (Demo.Active) return;
                AppSettings.Current.UpdateCheck = on;
                AppSettings.Current.Save();
            };
            hooks.CheckNow = delegate { return CheckForUpdates(true); };
            hooks.CanInstall = delegate { return available != null && !string.IsNullOrEmpty(available.ZipUrl) && Updater.InstallBlocker() == null; };
            hooks.Install = async delegate { await InstallUpdate(); };
            hooks.OpenPage = OpenReleasePage;
            AboutDialog.ShowFor(this, hooks);
        }

        /// <summary>Screenshots: Info-Fenster zeigt einen geprüften Stand, keine Hinweisleiste.</summary>
        public static UpdateStatus DemoUpdateStatus
        {
            get { return new UpdateStatus(UpdateKind.Current, "Aktuell", "Zuletzt geprüft " + DateTime.Now.ToString("dd.MM.yyyy")); }
        }

        string updateError;

        UpdateStatus UpdateState()
        {
            if (Demo.Active) return DemoUpdateStatus;
            DateTime lastCheck = AppSettings.Current.LastUpdateCheck;
            string last = lastCheck == DateTime.MinValue ? "" : "Zuletzt geprüft " + lastCheck.ToLocalTime().ToString("dd.MM.yyyy");
            if (available != null) return new UpdateStatus(UpdateKind.Available, "Version " + available.Version + " verfügbar", last);
            if (updateError != null) return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", updateError);
            if (lastCheck == DateTime.MinValue) return new UpdateStatus(UpdateKind.Unknown, "Noch nicht geprüft", "");
            return new UpdateStatus(UpdateKind.Current, "Aktuell", last);
        }

        // Neueste Version auf GitHub abfragen. Beim Start (manual = false) höchstens alle 20 Stunden und still.
        bool updateRunning;

        // Ergebnis einer Prüfung von Hand nur im Info-Fenster; im Hauptfenster erscheint nur bei neuer Version etwas
        async Task<UpdateStatus> CheckForUpdates(bool manual)
        {
            if (Demo.Active || updateRunning) return UpdateState(); // Screenshots: keine Prüfung
            AppSettings st = AppSettings.Current;
            if (!manual && (!st.UpdateCheck || (DateTime.UtcNow - st.LastUpdateCheck.ToUniversalTime()).TotalHours < 20)) return UpdateState();
            updateRunning = true;
            try
            {
                ReleaseInfo release = await Task.Run(() => Updater.GetLatest());
                updateError = null;
                st.LastUpdateCheck = DateTime.UtcNow;
                st.Save();
                if (Updater.IsNewer(release.Version))
                {
                    available = release;
                    ShowUpdateBar(true);
                    UpdateTrayMenu();
                    // Hinweis am Infobereich, einmal je Version und Programmstart; Klick startet das Update
                    if (tray != null && tray.Visible && balloonShownFor != release.Version)
                    {
                        balloonShownFor = release.Version;
                        balloonIsUpdate = true;
                        tray.ShowBalloonTip(10000, "Sync Pilot " + release.Version + " ist verfügbar",
                            "Klicken, um das Update zu installieren.", ToolTipIcon.Info);
                    }
                }
                else
                {
                    available = null;
                    ShowUpdateBar(false);
                    UpdateTrayMenu();
                }
            }
            catch (Exception ex)
            {
                // automatisch: still (nur im Info-Fenster sichtbar); von Hand: Fehlermeldung im Info-Fenster
                updateError = ex.Message;
            }
            finally { updateRunning = false; }
            return UpdateState();
        }

        void ShowUpdateBar(bool show)
        {
            show = show && available != null;
            if (show)
            {
                updateBar.Text = "Neue Version " + available.Version + " verfügbar – installiert ist " + AppInfo.Version + ".";
                string blocker = Updater.InstallBlocker();
                // im Projektordner wird nie ersetzt: "Jetzt aktualisieren" öffnet dort die Release-Seite
                updateBar.Install.Enabled = true;
                updateTips.SetToolTip(updateBar.Install, blocker != null ? blocker + " Öffnet die Release-Seite." :
                    "ZIP herunterladen, Sync Pilot beenden, EXE und Liesmich ersetzen und neu starten.\r\nEinstellungen bleiben erhalten.");
            }
            bool wasVisible = updateHost.Visible;
            updateHost.Visible = show;
            // Fenster um die Leiste vergrößern, soweit der Bildschirm reicht
            if (show && !wasVisible && WindowState == FormWindowState.Normal)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int grow = Math.Min(updateHost.Height, Math.Max(0, area.Bottom - Bottom));
                if (grow > 0) Height += grow;
            }
            updateHost.SendToBack(); // zuerst andocken: oben über der Seite
        }

        readonly ToolTip updateTips = new ToolTip();
        Panel noticeHost;
        Label notice;
        System.Windows.Forms.Timer noticeTimer;

        /// <summary>Kurze Meldung oben im Inhalt (ok = grün, sonst Warnfarbe); Klick blendet sie aus.</summary>
        public void ShowNotice(string text, bool ok)
        {
            notice.Text = (ok ? "\u2713  " : "\u26A0  ") + text;
            notice.BackColor = ok ? Theme.OkBg : Theme.WarnBg;
            notice.ForeColor = ok ? Theme.Ok : Theme.Warn;
            noticeHost.Visible = true;
            noticeHost.SendToBack();
            updateHost.SendToBack();
            noticeTimer.Stop();
            noticeTimer.Start();
        }
        ContextMenuStrip trayMenu;
        ModernMenuBuilder trayBuilder;
        ToolStripItem trayUpdateItem, trayUpdateSep;
        string balloonShownFor;   // Hinweis am Infobereich: einmal je Version und Programmstart
        bool balloonIsUpdate;

        // Oben im Tray-Menü fett "Update auf x installieren …" (nur wenn eine neuere Version gefunden wurde)
        void UpdateTrayMenu()
        {
            if (trayMenu == null) return;
            if (trayUpdateItem != null) { trayMenu.Items.Remove(trayUpdateItem); trayMenu.Items.Remove(trayUpdateSep); trayUpdateItem = trayUpdateSep = null; }
            if (available == null) return;
            GlyphMenuItem it = trayBuilder.Item("Update auf " + available.Version + " installieren \u2026", '\uE896', async delegate
            {
                ShowFromTray();
                await InstallUpdate();
            });
            it.Font = new Font(it.Font, FontStyle.Bold);
            int at = trayMenu.Items.Count > 0 && trayMenu.Items[0] is MenuSpacer ? 1 : 0;
            trayUpdateSep = new ToolStripSeparator();
            trayMenu.Items.Insert(at, trayUpdateSep);
            trayMenu.Items.Insert(at, it);
            trayUpdateItem = it;
        }

        void OpenReleasePage()
        {
            Ui.Open(available != null && !string.IsNullOrEmpty(available.PageUrl) ? available.PageUrl : Updater.ReleasesPage);
        }

        async Task InstallUpdate()
        {
            if (updating || available == null) return;
            if ((current != null && current.Busy) || RemoteRun.SomeBusy || Triggers.SomeBusy)
            {
                MessageBox.Show(this, "Es läuft gerade ein Abgleich. Bitte warten, bis er fertig ist, und dann aktualisieren.", "Update",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Updater.InstallBlocker() != null) { OpenReleasePage(); return; }
            if (MessageBox.Show(this, "Sync Pilot wird beendet, auf Version " + available.Version + " aktualisiert und neu gestartet. Einstellungen bleiben erhalten.",
                    "Update", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            updating = true;
            foreach (FlatButton b in new FlatButton[] { updateBar.Install, updateBar.Page, updateBar.Later }) b.Enabled = false;
            string text = updateBar.Text;
            try
            {
                ReleaseInfo rel = available;
                await Updater.Install(rel, delegate (int percent)
                {
                    try { BeginInvoke(new Action(delegate { updateBar.Text = "Lade Version " + rel.Version + " … " + percent + " %"; })); } catch { }
                });
                updateBar.Text = "Update wird installiert – Sync Pilot startet gleich neu.";
                // ganz beenden (nicht nur in den Infobereich), damit das Hilfsskript die EXE ersetzen kann
                exitRequested = true;
                closeAfterCancel = false;
                if (!Visible) Show();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                updateBar.Text = text;
                foreach (FlatButton b in new FlatButton[] { updateBar.Page, updateBar.Later }) b.Enabled = true;
                updateBar.Install.Enabled = true;
            }
            finally { updating = false; }
        }

        // nach dem Neustart durch das Hilfsskript: Ergebnis zeigen
        void ShowUpdateResult()
        {
            if (!AfterUpdate) return;
            AfterUpdate = false;
            string result = Updater.LastResult();
            if (result == null) return;
            if (result.StartsWith("OK"))
            {
                string v = result.Substring(2).Trim();
                if (tray != null) { balloonIsUpdate = false; tray.ShowBalloonTip(6000, "Sync Pilot", "Update auf Version " + v + " installiert.", ToolTipIcon.Info); }
            }
            else
                MessageBox.Show(this, "Das Update ist fehlgeschlagen: " + result.Replace("FEHLER", "").Trim() + "\r\n\r\nDie bisherige Version läuft weiter.",
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Automatisch (bei einem Ereignis) gestarteter Lauf ist fertig: Ansicht auffrischen, Fehler melden
        void TriggerFinished(SyncJob job, HistoryEntry h, string reason)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (current != null && !current.Busy)
                    {
                        if (current.NavKey == "tasks") ShowTasks();
                        else if (current.NavKey == "history") Navigate(new HistoryView(this));
                    }
                    if (tray != null && !h.Cancelled && (!h.IsOk || (h.Result ?? "").StartsWith("Stand fehlte")))
                    {
                        balloonIsUpdate = false;
                        tray.ShowBalloonTip(5000, "Sync Pilot: " + job.Name, reason + " \u2013 " + (h.Result ?? "Fehler"), ToolTipIcon.Warning);
                    }
                }));
            }
            catch { }
        }

        public void SaveJobs()
        {
            Triggers.Apply(Jobs);
            try { Store.SaveJobs(Jobs); }
            catch (Exception ex)
            {
                MessageBox.Show("Speichern fehlgeschlagen:\n" + ex.Message, "Sync Pilot", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void DeleteJob(SyncJob job)
        {
            Scheduler.Remove(job);
            StateStore.Delete(job.Id);
            Jobs.Remove(job);
            SaveJobs();
        }

        public void ViewIdle()
        {
            if (closeAfterCancel) BeginInvoke(new Action(Close));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Fenster schließen = im Infobereich weiterlaufen (außer bei "Beenden" oder Windows-Abmeldung)
            if (!exitRequested && tray != null && e.CloseReason == CloseReason.UserClosing && AppSettings.Current.Background)
            {
                e.Cancel = true;
                Hide();
                UpdateTray();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    balloonIsUpdate = false;
                    tray.ShowBalloonTip(3000, "Sync Pilot", "L\u00e4uft im Infobereich weiter. Beenden \u00fcber das Symbol (Rechtsklick).", ToolTipIcon.Info);
                }
                return;
            }
            if (current != null && current.Busy)
            {
                DialogResult r = MessageBox.Show("Es läuft noch ein Vorgang. Abbrechen und beenden?", "Sync Pilot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                e.Cancel = true;
                if (r == DialogResult.Yes)
                {
                    closeAfterCancel = true;
                    current.RequestCancel();
                }
                return;
            }
            base.OnFormClosing(e);
        }
    }
}