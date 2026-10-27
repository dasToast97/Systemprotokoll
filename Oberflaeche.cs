// Fenster der EXE: Startdialog (Zeitraum + Speicherort), Fortschritt, Warten.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Ui
{
    public static readonly Font Basis = new Font("Segoe UI", 9.5f);
    public static readonly Font Fett = new Font("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font Titel = new Font("Segoe UI", 13f, FontStyle.Bold);
    public static readonly Color Gruen = Color.FromArgb(30, 125, 50);
    public static readonly Color Orange = Color.FromArgb(178, 106, 0);
    public static readonly Color Rot = Color.FromArgb(198, 40, 40);
    public static readonly Color Grau = Color.FromArgb(110, 110, 110);

    public static string Version { get { return Updater.CurrentVersion.ToString(3); } }

    /// Nur fuer automatische Tests: speichert ein Bild des Fensters, wenn SYSTEMPROTOKOLL_SCREENSHOTS gesetzt ist.
    public static void Screenshot(Form f, string name)
    {
        string dir = Environment.GetEnvironmentVariable("SYSTEMPROTOKOLL_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch { }
    }

    /// Nur fuer Tests: Fenster kurz anzeigen, fotografieren, schliessen.
    public static void Vorschau(Form f, string name)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(20, 20);
        f.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);
        Application.DoEvents();
        Screenshot(f, name);
        f.Close();
        f.Dispose();
    }

    public static Label Label(Control parent, string text, int x, int y, int w, int h)
    {
        var l = new Label();
        l.Text = text; l.SetBounds(x, y, w, h); l.AutoEllipsis = true;
        parent.Controls.Add(l);
        return l;
    }

    public static Button Button(Control parent, string text, int x, int y, int w, int h)
    {
        var b = new Button();
        b.Text = text; b.SetBounds(x, y, w, h);
        parent.Controls.Add(b);
        return b;
    }

    static string SettingsDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Systemprotokoll"); }
    }

    public static string LadeEinstellung(string name, string standard)
    {
        try
        {
            string f = Path.Combine(SettingsDir, name + ".txt");
            if (File.Exists(f)) { string v = File.ReadAllText(f).Trim(); if (v.Length > 0) return v; }
        }
        catch { }
        return standard;
    }

    public static void SpeichereEinstellung(string name, string wert)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(Path.Combine(SettingsDir, name + ".txt"), wert); }
        catch { }
    }
}

/// Startfenster: Zeitraum, Speicherort, Schnellmodus.
class StartDialog : Form
{
    readonly DateTimePicker dStart, dEnde;
    readonly RadioButton rEnde, rDauer;
    readonly NumericUpDown nDauer;
    readonly CheckBox cSchnell;
    readonly TextBox tZiel;
    readonly Label lFehler;

    public DateTime Start, Ende;
    public bool Schnell;
    public string Ziel;

    public StartDialog(bool schnellVorschlag, string zielVorschlag)
    {
        Text = "Systemzustands-Protokoll " + Ui.Version;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Basis;
        ClientSize = new Size(500, 430);
        ShowIcon = false;

        var titel = Ui.Label(this, "Neues Protokoll erstellen", 18, 14, 460, 28); titel.Font = Ui.Titel;
        Ui.Label(this, "Welcher Zeitraum soll in den Ereignisprotokollen ausgewertet werden?", 18, 46, 470, 20).ForeColor = Ui.Grau;

        // Zeitraum
        var gZeit = new GroupBox(); gZeit.Text = "Zeitraum"; gZeit.SetBounds(18, 74, 464, 182); Controls.Add(gZeit);
        Ui.Label(gZeit, "Start:", 14, 30, 80, 22);
        dStart = new DateTimePicker();
        dStart.Format = DateTimePickerFormat.Custom; dStart.CustomFormat = "dd.MM.yyyy   HH:mm";
        dStart.SetBounds(100, 26, 190, 26); dStart.Value = DateTime.Now.AddDays(-30);
        gZeit.Controls.Add(dStart);

        rEnde = new RadioButton(); rEnde.Text = "Ende:"; rEnde.Checked = true; rEnde.SetBounds(14, 64, 82, 24); gZeit.Controls.Add(rEnde);
        dEnde = new DateTimePicker();
        dEnde.Format = DateTimePickerFormat.Custom; dEnde.CustomFormat = "dd.MM.yyyy   HH:mm";
        dEnde.SetBounds(100, 62, 190, 26); dEnde.Value = DateTime.Now;
        gZeit.Controls.Add(dEnde);
        var bJetzt = Ui.Button(gZeit, "Jetzt", 298, 61, 70, 28);

        rDauer = new RadioButton(); rDauer.Text = "Dauer:"; rDauer.SetBounds(14, 100, 82, 24); gZeit.Controls.Add(rDauer);
        nDauer = new NumericUpDown(); nDauer.Minimum = 1; nDauer.Maximum = 3650; nDauer.Value = 30; nDauer.Enabled = false;
        nDauer.SetBounds(100, 99, 80, 26); gZeit.Controls.Add(nDauer);
        Ui.Label(gZeit, "Tage ab Start", 188, 102, 120, 22);

        Ui.Label(gZeit, "Letzte:", 14, 140, 62, 22).ForeColor = Ui.Grau;
        int x = 78;
        foreach (int t in new[] { 7, 30, 90, 365 })
        {
            var b = Ui.Button(gZeit, t == 365 ? "1 Jahr" : t + " Tage", x, 136, 70, 28);
            b.Tag = t; x += 74;
            b.Click += delegate(object s, EventArgs e)
            {
                rEnde.Checked = true;
                dEnde.Value = DateTime.Now;
                dStart.Value = DateTime.Now.AddDays(-(int)((Button)s).Tag);
            };
        }
        var bAlles = Ui.Button(gZeit, "Alles", x, 136, 70, 28);

        // Speicherort
        var gZiel = new GroupBox(); gZiel.Text = "Speicherort"; gZiel.SetBounds(18, 264, 464, 66); Controls.Add(gZiel);
        tZiel = new TextBox(); tZiel.Text = zielVorschlag; tZiel.SetBounds(14, 27, 318, 26); gZiel.Controls.Add(tZiel);
        var bZiel = Ui.Button(gZiel, "Durchsuchen …", 338, 25, 112, 28);

        cSchnell = new CheckBox(); cSchnell.Text = "Schnellmodus (ohne Systemdatei- und Dateisystem-Prüfung)";
        cSchnell.Checked = schnellVorschlag; cSchnell.SetBounds(20, 338, 460, 24); Controls.Add(cSchnell);

        lFehler = Ui.Label(this, "", 20, 364, 460, 20); lFehler.ForeColor = Ui.Rot;

        var bOk = Ui.Button(this, "Protokoll starten", 252, 388, 136, 32);
        bOk.Font = Ui.Fett;
        var bAbbr = Ui.Button(this, "Abbrechen", 394, 388, 88, 32);
        bAbbr.DialogResult = DialogResult.Cancel;
        AcceptButton = bOk; CancelButton = bAbbr;

        bJetzt.Click += delegate { rEnde.Checked = true; dEnde.Value = DateTime.Now; };
        rEnde.CheckedChanged += delegate { dEnde.Enabled = rEnde.Checked; nDauer.Enabled = !rEnde.Checked; };
        bAlles.Click += delegate
        {
            DateTime aelteste = DateTime.Now.AddYears(-1);
            try
            {
                using (var r = new EventLogReader(new EventLogQuery("System", PathType.LogName)))
                using (var ev = r.ReadEvent())
                    if (ev != null && ev.TimeCreated.HasValue) aelteste = ev.TimeCreated.Value;
            }
            catch { }
            rEnde.Checked = true; dEnde.Value = DateTime.Now;
            dStart.Value = aelteste < dStart.MinDate ? dStart.MinDate : aelteste;
        };
        bZiel.Click += delegate
        {
            using (var fb = new FolderBrowserDialog())
            {
                fb.Description = "Ordner wählen, in dem das Protokoll gespeichert wird";
                fb.ShowNewFolderButton = true;
                if (Directory.Exists(tZiel.Text)) fb.SelectedPath = tZiel.Text;
                if (fb.ShowDialog(this) == DialogResult.OK) tZiel.Text = fb.SelectedPath;
            }
        };
        bOk.Click += delegate
        {
            DateTime s = dStart.Value;
            DateTime e = rEnde.Checked ? dEnde.Value : s.AddDays((double)nDauer.Value);
            if (e <= s) { lFehler.Text = "Das Ende muss nach dem Start liegen."; return; }
            if (s > DateTime.Now) { lFehler.Text = "Der Start darf nicht in der Zukunft liegen."; return; }
            string ziel = tZiel.Text.Trim();
            if (ziel.Length == 0) { lFehler.Text = "Bitte einen Speicherort angeben."; return; }
            try { ziel = Path.GetFullPath(ziel); Directory.CreateDirectory(ziel); }
            catch (Exception ex) { lFehler.Text = "Speicherort nicht verwendbar: " + ex.Message; return; }
            Start = s; Ende = e; Schnell = cSchnell.Checked; Ziel = ziel;
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}

/// Fortschrittsfenster: startet das Skript unsichtbar und zeigt Schritte, Balken und Ergebnis.
class FortschrittForm : Form
{
    class Schritt { public string Text; public bool Fertig; }

    readonly ProcessStartInfo psi;
    readonly bool autoClose;
    Process proc;
    readonly DateTime startZeit = DateTime.Now;
    readonly System.Windows.Forms.Timer uhr = new System.Windows.Forms.Timer();

    readonly Label lTitel, lSchritt, lProzent, lZeit;
    readonly ListBox liste;
    readonly ProgressBar bar;
    readonly TextBox details;
    readonly Button bDetails, bAbbrechen, bBericht, bOrdner, bSchliessen;
    readonly Panel ergebnis;
    readonly Label lBanner, lZahlen, lOrt;
    readonly TextBox tHash;

    readonly StringBuilder log = new StringBuilder();
    string berichtPfad, ordnerPfad;
    bool laeuft, ergebnisDa, abgebrochen, beendet, vorschauLauf;
    int prozent;
    public int ExitCode = 1;

    const int Breite = 600, HoeheKlein = 440, DetailHoehe = 170;

    public FortschrittForm(ProcessStartInfo info, bool autoClose)
    {
        psi = info; this.autoClose = autoClose;
        Text = "Systemzustands-Protokoll " + Ui.Version;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Basis;
        ShowIcon = false;
        ClientSize = new Size(Breite, HoeheKlein);

        lTitel = Ui.Label(this, "Systemzustand wird erfasst …", 18, 14, 560, 30); lTitel.Font = Ui.Titel;

        liste = new ListBox();
        liste.SetBounds(18, 52, 564, 236);
        liste.BorderStyle = BorderStyle.FixedSingle;
        liste.DrawMode = DrawMode.OwnerDrawFixed;
        liste.ItemHeight = 24;
        liste.SelectionMode = SelectionMode.None;
        liste.IntegralHeight = false;
        liste.DrawItem += ZeichneSchritt;
        Controls.Add(liste);

        ergebnis = new Panel(); ergebnis.SetBounds(18, 52, 564, 236); ergebnis.Visible = false; Controls.Add(ergebnis);
        lBanner = Ui.Label(ergebnis, "", 0, 0, 564, 48);
        lBanner.Font = new Font("Segoe UI", 12f, FontStyle.Bold); lBanner.ForeColor = Color.White;
        lBanner.TextAlign = ContentAlignment.MiddleLeft; lBanner.Padding = new Padding(12, 0, 0, 0);
        lZahlen = Ui.Label(ergebnis, "", 0, 58, 564, 22); lZahlen.Font = Ui.Fett;
        Ui.Label(ergebnis, "Gespeichert in:", 0, 92, 564, 20).ForeColor = Ui.Grau;
        lOrt = Ui.Label(ergebnis, "", 0, 112, 564, 40);
        Ui.Label(ergebnis, "SHA256-Prüfsumme des ZIP-Archivs (als Nachweis notieren oder mitschicken):", 0, 160, 564, 20).ForeColor = Ui.Grau;
        tHash = new TextBox(); tHash.ReadOnly = true; tHash.SetBounds(0, 182, 470, 26); tHash.Font = new Font("Consolas", 9f);
        ergebnis.Controls.Add(tHash);
        var bKopie = Ui.Button(ergebnis, "Kopieren", 476, 180, 88, 28);
        bKopie.Click += delegate { try { Clipboard.SetText(tHash.Text); } catch { } };

        lSchritt = Ui.Label(this, "Starte …", 18, 300, 470, 22);
        lProzent = Ui.Label(this, "0 %", 492, 300, 90, 22); lProzent.TextAlign = ContentAlignment.TopRight; lProzent.Font = Ui.Fett;
        bar = new ProgressBar(); bar.SetBounds(18, 326, 564, 24); bar.Minimum = 0; bar.Maximum = 100; Controls.Add(bar);
        lZeit = Ui.Label(this, "Laufzeit 00:00", 18, 356, 300, 20); lZeit.ForeColor = Ui.Grau;

        bDetails = Ui.Button(this, "Details ▾", 18, 394, 100, 32);
        bAbbrechen = Ui.Button(this, "Abbrechen", 482, 394, 100, 32);
        bBericht = Ui.Button(this, "Bericht öffnen", 228, 394, 124, 32); bBericht.Visible = false; bBericht.Font = Ui.Fett;
        bOrdner = Ui.Button(this, "Ordner öffnen", 358, 394, 118, 32); bOrdner.Visible = false;
        bSchliessen = Ui.Button(this, "Schließen", 482, 394, 100, 32); bSchliessen.Visible = false;

        details = new TextBox();
        details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Both; details.WordWrap = false;
        details.Font = new Font("Consolas", 8.5f);
        details.SetBounds(18, HoeheKlein - 4, 564, DetailHoehe - 14);
        details.Visible = false;
        Controls.Add(details);

        bDetails.Click += delegate { ZeigeDetails(!details.Visible); };
        bAbbrechen.Click += delegate { Close(); };
        bSchliessen.Click += delegate { Close(); };
        bBericht.Click += delegate { Oeffne(berichtPfad); };
        bOrdner.Click += delegate { Oeffne(ordnerPfad); };

        uhr.Interval = 1000;
        uhr.Tick += delegate { AktualisiereZeit(); };
        Shown += delegate { Starte(); };
        FormClosing += BeimSchliessen;
    }

    void ZeigeDetails(bool an)
    {
        details.Visible = an;
        ClientSize = new Size(Breite, an ? HoeheKlein + DetailHoehe : HoeheKlein);
        bDetails.Text = an ? "Details ▴" : "Details ▾";
        if (an) { details.Text = log.ToString(); details.SelectionStart = details.TextLength; details.ScrollToCaret(); }
    }

    void AktualisiereZeit()
    {
        TimeSpan t = DateTime.Now - startZeit;
        lZeit.Text = "Laufzeit " + ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
    }

    static void Oeffne(string pfad)
    {
        if (string.IsNullOrEmpty(pfad)) return;
        try { Process.Start(new ProcessStartInfo(pfad) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Konnte nicht geöffnet werden:\n" + ex.Message, "Systemprotokoll"); }
    }

    void ZeichneSchritt(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var s = (Schritt)liste.Items[e.Index];
        e.Graphics.FillRectangle(SystemBrushes.Window, e.Bounds);
        Rectangle symbol = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, 22, e.Bounds.Height);
        Rectangle text = new Rectangle(e.Bounds.X + 32, e.Bounds.Y, e.Bounds.Width - 36, e.Bounds.Height);
        var fmt = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (s.Fertig)
        {
            TextRenderer.DrawText(e.Graphics, "✓", Ui.Fett, symbol, Ui.Gruen, fmt);
            TextRenderer.DrawText(e.Graphics, s.Text, Ui.Basis, text, Ui.Grau, fmt);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, "►", Ui.Basis, symbol, SystemColors.Highlight, fmt);
            TextRenderer.DrawText(e.Graphics, s.Text, Ui.Fett, text, SystemColors.WindowText, fmt);
        }
    }

    void Starte()
    {
        uhr.Start();
        try
        {
            proc = new Process();
            proc.StartInfo = psi;
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) NachUi(delegate { Zeile(e.Data, false); }); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) NachUi(delegate { Zeile(e.Data, true); }); };
            proc.Exited += delegate
            {
                try { proc.WaitForExit(); } catch { }   // restliche Ausgabe einsammeln
                NachUi(Beendet);
            };
            proc.Start();
            laeuft = true;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            log.AppendLine("Start fehlgeschlagen: " + ex.Message);
            Beendet();
        }
    }

    void NachUi(MethodInvoker m)
    {
        try { if (!IsDisposed) BeginInvoke(m); } catch { }
    }

    void Zeile(string zeile, bool fehler)
    {
        if (zeile.StartsWith("##P\t"))
        {
            string[] t = zeile.Split('\t');
            int p;
            if (t.Length >= 3)
            {
                if (int.TryParse(t[1], out p) && p >= 0) { prozent = Math.Min(100, Math.Max(prozent, p)); bar.Value = prozent; lProzent.Text = prozent + " %"; }
                if (p >= 42 && !vorschauLauf) { vorschauLauf = true; Ui.Screenshot(this, "2-fortschritt"); }
                if (liste.Items.Count > 0) ((Schritt)liste.Items[liste.Items.Count - 1]).Fertig = true;
                if (t[2] != "Fertig")
                {
                    liste.Items.Add(new Schritt { Text = t[2] });
                    liste.TopIndex = Math.Max(0, liste.Items.Count - liste.ClientSize.Height / liste.ItemHeight);
                    lSchritt.Text = t[2] + " …";
                }
                liste.Invalidate();
                log.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t[2]);
            }
        }
        else if (zeile.StartsWith("##R\t"))
        {
            string[] t = zeile.Split('\t');
            if (t.Length >= 11) { ergebnisDa = true; ZeigeErgebnis(t); }
        }
        else
        {
            log.AppendLine((fehler ? "! " : "  ") + zeile);
        }
        if (details.Visible) { details.Text = log.ToString(); details.SelectionStart = details.TextLength; details.ScrollToCaret(); }
    }

    void ZeigeErgebnis(string[] t)
    {
        // t: ##R, klasse, gesamt, krit, warn, hinw, ok, ordner, zip, hash, bericht, zeitraum
        string klasse = t[1];
        lBanner.BackColor = klasse == "krit" ? Ui.Rot : klasse == "warn" ? Ui.Orange : Ui.Gruen;
        lBanner.Text = t[2];
        lZahlen.Text = "Kritisch: " + t[3] + "     Warnungen: " + t[4] + "     Hinweise: " + t[5] + "     OK: " + t[6];
        ordnerPfad = t[7];
        lOrt.Text = t[7] + "\n" + t[8];
        tHash.Text = t[9];
        berichtPfad = t[10];
    }

    void Beendet()
    {
        if (beendet) return;
        beendet = true;
        laeuft = false;
        uhr.Stop();
        AktualisiereZeit();
        try { if (proc != null) ExitCode = proc.ExitCode; } catch { }
        if (abgebrochen) return;

        bAbbrechen.Visible = false;
        bSchliessen.Visible = true;
        AcceptButton = bSchliessen;

        if (ergebnisDa)
        {
            ExitCode = 0;
            bar.Value = 100; lProzent.Text = "100 %";
            lTitel.Text = "Protokoll erstellt";
            lSchritt.Text = "Alle Prüfungen abgeschlossen.";
            liste.Visible = false; ergebnis.Visible = true;
            bBericht.Visible = true; bOrdner.Visible = true;
            bBericht.Focus();
            Refresh();
            Ui.Screenshot(this, "3-ergebnis");
            try { FlashWindow(); } catch { }
            if (autoClose)
            {
                var t = new System.Windows.Forms.Timer(); t.Interval = 1500;
                t.Tick += delegate { t.Stop(); Close(); };
                t.Start();
            }
        }
        else
        {
            if (ExitCode == 0) ExitCode = 1;
            lTitel.Text = "Protokoll konnte nicht erstellt werden";
            lTitel.ForeColor = Ui.Rot;
            lSchritt.Text = "Details siehe unten (Exit-Code " + ExitCode + ").";
            ZeigeDetails(true);
            Refresh();
            Ui.Screenshot(this, "3-fehler");
            if (autoClose)
            {
                var t = new System.Windows.Forms.Timer(); t.Interval = 1500;
                t.Tick += delegate { t.Stop(); Close(); };
                t.Start();
            }
        }
    }

    void FlashWindow() { Activate(); }   // Fenster nach vorne holen, wenn fertig

    void BeimSchliessen(object sender, FormClosingEventArgs e)
    {
        if (!laeuft) return;
        if (!autoClose && MessageBox.Show(this, "Die Prüfung läuft noch. Wirklich abbrechen?\nEs wird kein vollständiges Protokoll erstellt.",
                "Abbrechen?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        abgebrochen = true;
        laeuft = false;
        ExitCode = 3;
        try
        {
            var k = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + proc.Id);
            k.CreateNoWindow = true; k.UseShellExecute = false;
            using (var p = Process.Start(k)) p.WaitForExit(10000);
        }
        catch { }
    }
}

/// Kleines Wartefenster (Update-Suche, Download).
class WarteForm : Form
{
    readonly Label text;
    readonly ProgressBar bar;

    public WarteForm(string meldung, bool marquee)
    {
        Text = "Systemzustands-Protokoll";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false; ShowIcon = false; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Basis;
        ClientSize = new Size(400, 92);
        text = Ui.Label(this, meldung, 18, 16, 364, 22);
        bar = new ProgressBar(); bar.SetBounds(18, 46, 364, 22);
        bar.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        bar.MarqueeAnimationSpeed = 30;
        Controls.Add(bar);
    }

    public void SetzeProzent(int p, string meldung)
    {
        bar.Style = ProgressBarStyle.Continuous;
        bar.Value = Math.Max(0, Math.Min(100, p));
        if (meldung != null) text.Text = meldung;
    }

    /// Fuehrt arbeit im Hintergrund aus, solange das Fenster angezeigt wird.
    public static void Ausfuehren(string meldung, bool marquee, Action<WarteForm> arbeit)
    {
        using (var f = new WarteForm(meldung, marquee))
        {
            f.Shown += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { arbeit(f); }
                    finally { try { f.BeginInvoke((MethodInvoker)f.Close); } catch { } }
                });
            };
            f.ShowDialog();
        }
    }
}
