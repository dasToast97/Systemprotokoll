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

/// Startfenster: Modus (Protokoll/Reparatur), Reparaturauswahl, Zeitraum, Speicherort.
class StartDialog : Form
{
    readonly RadioButton rProtokoll, rReparatur;
    readonly GroupBox gWas, gRep, gZeit, gZiel;
    readonly DateTimePicker dStart, dEnde;
    readonly RadioButton rEnde, rDauer;
    readonly NumericUpDown nDauer;
    readonly CheckBox cSchnell, cWieder;
    readonly TextBox tZiel;
    readonly Label lFehler;
    readonly Button bOk, bAbbr;
    readonly List<CheckBox> repBoxen = new List<CheckBox>();

    public DateTime Start, Ende;
    public bool Schnell, Reparieren, Wiederherstellungspunkt;
    public string Ziel, Reparaturen;

    public static readonly string[,] Reparaturliste =
    {
        { "System",       "Systemdateien (DISM + SFC)", "Repariert beschädigte Windows-Systemdateien. Repariert beschädigte Windows-Dateien und den Komponentenspeicher. Dauert 10–40 Minuten, braucht Internet." },
        { "Datentraeger", "Datenträger / Dateisystem",          "Prüft alle Laufwerke und behebt Dateisystemfehler. Systemlaufwerk: Reparatur beim nächsten Neustart." },
        { "Treiber",      "Treiber und Problemgeräte",          "Startet Geräte mit Fehler im Geräte-Manager neu und installiert sie bei Bedarf neu." },
        { "Update",       "Windows Update zurücksetzen",        "Startet die Update-Dienste neu und leert den Update-Cache (alte Ordner werden gesichert)." },
        { "Netzwerk",     "Netzwerk zurücksetzen",              "Setzt Winsock, TCP/IP und DNS-Cache zurück. Wirksam nach Neustart; feste IP-Einstellungen ggf. neu eintragen." },
        { "Komponenten",  "Komponentenspeicher bereinigen",     "Entfernt veraltete Windows-Komponenten (DISM StartComponentCleanup)." },
        { "Druck",        "Druckwarteschlange leeren",          "Startet den Druckdienst neu und entfernt hängende Druckaufträge." },
        { "Zeit",         "Uhrzeit synchronisieren",            "Gleicht die Uhrzeit mit dem Zeitserver ab." },
    };
    static readonly string StandardAuswahl = "System,Datentraeger,Treiber";

    const int Breite = 520;

    public StartDialog(bool schnellVorschlag, string zielVorschlag, bool reparaturVorschlag = false)
    {
        Text = "Systemzustands-Protokoll " + Ui.Version;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Basis;
        ShowIcon = false;
        var tipp = new ToolTip(); tipp.AutoPopDelay = 15000;

        var titel = Ui.Label(this, "Was möchtest du tun?", 18, 14, 480, 28); titel.Font = Ui.Titel;

        // Modus
        gWas = new GroupBox(); gWas.SetBounds(18, 46, 484, 76); Controls.Add(gWas);
        rProtokoll = new RadioButton(); rProtokoll.Text = "Nur Protokoll erstellen – ändert nichts am System";
        rProtokoll.SetBounds(14, 16, 460, 24); rProtokoll.Checked = !reparaturVorschlag; gWas.Controls.Add(rProtokoll);
        rReparatur = new RadioButton(); rReparatur.Text = "Reparieren – mit Protokoll vorher und nachher";
        rReparatur.SetBounds(14, 42, 460, 24); rReparatur.Checked = reparaturVorschlag; gWas.Controls.Add(rReparatur);

        // Reparaturen
        gRep = new GroupBox(); gRep.Text = "Reparaturen"; gRep.SetBounds(18, 130, 484, 206); Controls.Add(gRep);
        var gewaehlt = new List<string>(Ui.LadeEinstellung("reparaturen", StandardAuswahl).Split(','));
        for (int i = 0; i < Reparaturliste.GetLength(0); i++)
        {
            var c = new CheckBox();
            c.Text = Reparaturliste[i, 1]; c.Tag = Reparaturliste[i, 0];
            c.SetBounds(i < 4 ? 14 : 250, 24 + (i % 4) * 26, 230, 24);
            c.Checked = gewaehlt.Contains(Reparaturliste[i, 0]);
            tipp.SetToolTip(c, Reparaturliste[i, 2]);
            gRep.Controls.Add(c); repBoxen.Add(c);
        }
        cWieder = new CheckBox(); cWieder.Text = "Vorher einen Wiederherstellungspunkt erstellen";
        cWieder.Checked = Ui.LadeEinstellung("wiederherstellungspunkt", "1") == "1";
        cWieder.SetBounds(14, 134, 460, 24); gRep.Controls.Add(cWieder);
        tipp.SetToolTip(cWieder, "Damit lässt sich Windows über die Systemwiederherstellung auf den Stand vor der Reparatur zurücksetzen.");
        var hinweis = Ui.Label(gRep, "Maus über eine Option halten für Details. Manche Reparaturen werden erst nach einem Neustart wirksam.", 14, 160, 460, 40);
        hinweis.ForeColor = Ui.Grau; hinweis.AutoEllipsis = false;

        // Zeitraum
        gZeit = new GroupBox(); gZeit.Text = "Zeitraum"; gZeit.SetBounds(18, 0, 484, 182); Controls.Add(gZeit);
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
            var b = Ui.Button(gZeit, t == 365 ? "1 Jahr" : t + " Tage", x, 136, 72, 28);
            b.Tag = t; x += 77;
            b.Click += delegate(object s, EventArgs e)
            {
                rEnde.Checked = true;
                dEnde.Value = DateTime.Now;
                dStart.Value = DateTime.Now.AddDays(-(int)((Button)s).Tag);
            };
        }
        var bAlles = Ui.Button(gZeit, "Alles", x, 136, 72, 28);

        // Speicherort
        gZiel = new GroupBox(); gZiel.Text = "Speicherort"; gZiel.SetBounds(18, 0, 484, 66); Controls.Add(gZiel);
        tZiel = new TextBox(); tZiel.Text = zielVorschlag; tZiel.SetBounds(14, 27, 338, 26); gZiel.Controls.Add(tZiel);
        var bZiel = Ui.Button(gZiel, "Durchsuchen …", 358, 25, 112, 28);

        cSchnell = new CheckBox(); cSchnell.Text = "Schnellmodus (ohne Systemdatei- und Dateisystem-Prüfung im Protokoll)";
        cSchnell.Checked = schnellVorschlag; Controls.Add(cSchnell);

        lFehler = new Label(); lFehler.ForeColor = Ui.Rot; lFehler.AutoEllipsis = true; Controls.Add(lFehler);

        bOk = new Button(); bOk.Font = Ui.Fett; Controls.Add(bOk);
        bAbbr = new Button(); bAbbr.Text = "Abbrechen"; bAbbr.DialogResult = DialogResult.Cancel; Controls.Add(bAbbr);
        AcceptButton = bOk; CancelButton = bAbbr;

        rProtokoll.CheckedChanged += delegate { Anordnen(); };
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
        bOk.Click += delegate { Bestaetigen(); };

        Anordnen();
    }

    /// Positioniert die Bereiche je nach Modus (Reparaturauswahl ein-/ausblenden).
    void Anordnen()
    {
        bool rep = rReparatur.Checked;
        gRep.Visible = rep;
        int y = rep ? gRep.Bottom + 8 : gWas.Bottom + 8;
        gZeit.Top = y; gZeit.Text = rep ? "Zeitraum für das Protokoll vor der Reparatur" : "Zeitraum";
        y = gZeit.Bottom + 8;
        gZiel.Top = y; y = gZiel.Bottom + 8;
        cSchnell.SetBounds(20, y, 480, 24); y += 26;
        lFehler.SetBounds(20, y, 480, 20); y += 24;
        bOk.Text = rep ? "Reparatur starten" : "Protokoll starten";
        bOk.SetBounds(Breite - 18 - 92 - 6 - 146, y, 146, 32);
        bAbbr.SetBounds(Breite - 18 - 92, y, 92, 32);
        ClientSize = new Size(Breite, y + 32 + 14);
        lFehler.Text = "";
    }

    void Bestaetigen()
    {
        DateTime s = dStart.Value;
        DateTime e = rEnde.Checked ? dEnde.Value : s.AddDays((double)nDauer.Value);
        if (e <= s) { lFehler.Text = "Das Ende muss nach dem Start liegen."; return; }
        if (s > DateTime.Now) { lFehler.Text = "Der Start darf nicht in der Zukunft liegen."; return; }
        string ziel = tZiel.Text.Trim();
        if (ziel.Length == 0) { lFehler.Text = "Bitte einen Speicherort angeben."; return; }
        try { ziel = Path.GetFullPath(ziel); Directory.CreateDirectory(ziel); }
        catch (Exception ex) { lFehler.Text = "Speicherort nicht verwendbar: " + ex.Message; return; }

        var auswahl = new List<string>();
        var namen = new List<string>();
        foreach (var c in repBoxen) if (c.Checked) { auswahl.Add((string)c.Tag); namen.Add("•  " + c.Text); }
        if (rReparatur.Checked)
        {
            if (auswahl.Count == 0) { lFehler.Text = "Bitte mindestens eine Reparatur auswählen."; return; }
            string text = "Folgende Reparaturen werden ausgeführt:\n\n" + string.Join("\n", namen.ToArray()) +
                          (cWieder.Checked ? "\n\nVorher wird ein Wiederherstellungspunkt erstellt." : "") +
                          "\n\nVorher und nachher wird automatisch ein Protokoll erstellt. Die Reparatur verändert das System und kann je nach Auswahl längere Zeit dauern." +
                          "\n\nJetzt starten?";
            if (MessageBox.Show(this, text, "Reparatur starten", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Ui.SpeichereEinstellung("reparaturen", string.Join(",", auswahl.ToArray()));
            Ui.SpeichereEinstellung("wiederherstellungspunkt", cWieder.Checked ? "1" : "0");
        }

        Start = s; Ende = e; Schnell = cSchnell.Checked; Ziel = ziel;
        Reparieren = rReparatur.Checked;
        Reparaturen = string.Join(",", auswahl.ToArray());
        Wiederherstellungspunkt = cWieder.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// Ergebnisse der einzelnen Phasen (aus den ##R/##RR-Zeilen der Skripte).
class PhasenKontext
{
    public readonly List<string[]> Protokolle = new List<string[]>();
    public string[] Reparatur;
    public DateTime ReparaturStart = DateTime.Now;

    /// Wertet eine Ergebniszeile aus. Rueckgabe true = Zeile war ein Ergebnis.
    public bool Verarbeite(string zeile)
    {
        if (zeile.StartsWith("##R\t")) { string[] t = zeile.Split('\t'); if (t.Length >= 11) { Protokolle.Add(t); return true; } }
        if (zeile.StartsWith("##RR\t")) { string[] t = zeile.Split('\t'); if (t.Length >= 7) { Reparatur = t; return true; } }
        return false;
    }
}

/// Ein Abschnitt des Ablaufs (ein Skriptaufruf).
class Phase
{
    public string Titel;
    public bool IstReparatur;
    public bool Pflicht;        // schlaegt sie fehl, wird abgebrochen
    public int Von, Bis;        // Anteil am Gesamtfortschritt
    public Func<PhasenKontext, ProcessStartInfo> Erzeuge;
}

/// Fortschrittsfenster: fuehrt die Phasen nacheinander unsichtbar aus und zeigt Schritte, Balken und Ergebnis.
class FortschrittForm : Form
{
    class Schritt { public string Text; public bool Fertig; public bool Kopf; }

    readonly List<Phase> phasen;
    readonly bool autoClose, reparaturModus;
    readonly string hauptordner;
    public readonly PhasenKontext Kontext = new PhasenKontext();
    int phaseNr = -1;
    Phase aktuell;
    Process proc;
    readonly DateTime startZeit = DateTime.Now;
    readonly System.Windows.Forms.Timer uhr = new System.Windows.Forms.Timer();

    readonly Label lTitel, lSchritt, lProzent, lZeit;
    readonly ListBox liste;
    readonly ProgressBar bar;
    readonly TextBox details;
    readonly Button bDetails, bAbbrechen, bBericht, bOrdner, bSchliessen;
    readonly Panel ergebnis;

    readonly StringBuilder log = new StringBuilder();
    string berichtPfad, ordnerPfad;
    bool laeuft, phaseErgebnis, abgebrochen, fertig, pflichtFehler, vorschauLauf;
    int prozent;
    public int ExitCode = 1;

    const int Breite = 600, ListeH = 250, HoeheKlein = 454, DetailHoehe = 170;

    public FortschrittForm(List<Phase> phasen, bool autoClose, string hauptordner)
    {
        this.phasen = phasen; this.autoClose = autoClose; this.hauptordner = hauptordner;
        reparaturModus = phasen.Count > 1;
        Text = "Systemzustands-Protokoll " + Ui.Version;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Basis;
        ShowIcon = false;
        ClientSize = new Size(Breite, HoeheKlein);

        lTitel = Ui.Label(this, reparaturModus ? "Reparatur läuft …" : "Systemzustand wird erfasst …", 18, 14, 560, 30);
        lTitel.Font = Ui.Titel;

        liste = new ListBox();
        liste.SetBounds(18, 52, 564, ListeH);
        liste.BorderStyle = BorderStyle.FixedSingle;
        liste.DrawMode = DrawMode.OwnerDrawFixed;
        liste.ItemHeight = 24;
        liste.SelectionMode = SelectionMode.None;
        liste.IntegralHeight = false;
        liste.DrawItem += ZeichneSchritt;
        Controls.Add(liste);

        ergebnis = new Panel(); ergebnis.SetBounds(18, 52, 564, ListeH); ergebnis.Visible = false; Controls.Add(ergebnis);

        int y = 52 + ListeH + 12;
        lSchritt = Ui.Label(this, "Starte …", 18, y, 470, 22);
        lProzent = Ui.Label(this, "0 %", 492, y, 90, 22); lProzent.TextAlign = ContentAlignment.TopRight; lProzent.Font = Ui.Fett;
        bar = new ProgressBar(); bar.SetBounds(18, y + 26, 564, 24); bar.Minimum = 0; bar.Maximum = 100; Controls.Add(bar);
        lZeit = Ui.Label(this, "Laufzeit 00:00", 18, y + 56, 300, 20); lZeit.ForeColor = Ui.Grau;

        int yb = HoeheKlein - 46;
        bDetails = Ui.Button(this, "Details ▾", 18, yb, 100, 32);
        bAbbrechen = Ui.Button(this, "Abbrechen", 482, yb, 100, 32);
        bBericht = Ui.Button(this, "Bericht öffnen", 228, yb, 124, 32); bBericht.Visible = false; bBericht.Font = Ui.Fett;
        bOrdner = Ui.Button(this, "Ordner öffnen", 358, yb, 118, 32); bOrdner.Visible = false;
        bSchliessen = Ui.Button(this, "Schließen", 482, yb, 100, 32); bSchliessen.Visible = false;

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
        uhr.Tick += delegate { AktualisiereZeit(); if (autoClose) TestLogSchreiben(); };
        Shown += delegate { uhr.Start(); NaechstePhase(); };
        FormClosing += BeimSchliessen;
    }

    void ZeigeDetails(bool an)
    {
        details.Visible = an;
        ClientSize = new Size(Breite, an ? HoeheKlein + DetailHoehe : HoeheKlein);
        bDetails.Text = an ? "Details ▴" : "Details ▾";
        if (an) { details.Text = log.ToString(); details.SelectionStart = details.TextLength; details.ScrollToCaret(); }
    }

    /// Nur Testlauf: Detail-Protokoll laufend ablegen (auch wenn etwas haengt).
    void TestLogSchreiben()
    {
        string dir = Environment.GetEnvironmentVariable("SYSTEMPROTOKOLL_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, (reparaturModus ? "log-reparatur" : "log-protokoll") + ".txt"), log.ToString()); } catch { }
    }

    void AktualisiereZeit()
    {
        TimeSpan t = DateTime.Now - startZeit;
        string s = ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
        if (t.TotalHours >= 1) s = ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        lZeit.Text = "Laufzeit " + s;
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
        var fmt = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (s.Kopf)
        {
            Rectangle r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, s.Text.ToUpperInvariant(), Ui.Fett, r, SystemColors.Highlight, fmt);
            return;
        }
        int einzug = reparaturModus ? 14 : 0;
        Rectangle symbol = new Rectangle(e.Bounds.X + 8 + einzug, e.Bounds.Y, 22, e.Bounds.Height);
        Rectangle text = new Rectangle(e.Bounds.X + 32 + einzug, e.Bounds.Y, e.Bounds.Width - 36 - einzug, e.Bounds.Height);
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

    void SchrittHinzufuegen(Schritt s)
    {
        if (liste.Items.Count > 0) ((Schritt)liste.Items[liste.Items.Count - 1]).Fertig = true;
        liste.Items.Add(s);
        liste.TopIndex = Math.Max(0, liste.Items.Count - liste.ClientSize.Height / liste.ItemHeight);
        liste.Invalidate();
    }

    void SetzeProzent(int p)
    {
        prozent = Math.Min(100, Math.Max(prozent, p));
        bar.Value = prozent;
        lProzent.Text = prozent + " %";
        if (prozent >= 45 && !vorschauLauf)
        {
            vorschauLauf = true;
            Refresh();
            Ui.Screenshot(this, reparaturModus ? "2-fortschritt-reparatur" : "2-fortschritt");
        }
    }

    void NaechstePhase()
    {
        phaseNr++;
        if (phaseNr >= phasen.Count) { Fertig(); return; }
        aktuell = phasen[phaseNr];
        phaseErgebnis = false;
        SetzeProzent(aktuell.Von);
        if (reparaturModus) SchrittHinzufuegen(new Schritt { Text = (phaseNr + 1) + ". " + aktuell.Titel, Kopf = true });
        if (aktuell.IstReparatur) { lTitel.Text = "Reparatur läuft …"; Kontext.ReparaturStart = DateTime.Now; }
        else if (reparaturModus) lTitel.Text = aktuell.Titel + " …";
        log.AppendLine("=== " + aktuell.Titel + " (" + DateTime.Now.ToString("HH:mm:ss") + ") ===");

        try
        {
            proc = new Process();
            proc.StartInfo = aktuell.Erzeuge(Kontext);
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) NachUi(delegate { Zeile(e.Data, false); }); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) NachUi(delegate { Zeile(e.Data, true); }); };
            var meinProc = proc;
            proc.Exited += delegate
            {
                try { meinProc.WaitForExit(); } catch { }   // restliche Ausgabe einsammeln
                NachUi(PhaseBeendet);
            };
            proc.Start();
            laeuft = true;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            log.AppendLine("Start fehlgeschlagen: " + ex.Message);
            PhaseBeendet();
        }
    }

    void PhaseBeendet()
    {
        if (abgebrochen || fertig) return;
        laeuft = false;
        int code = -1;
        try { code = proc.ExitCode; } catch { }
        if (!phaseErgebnis)
        {
            log.AppendLine("Phase \"" + aktuell.Titel + "\" ohne Ergebnis beendet (Exit-Code " + code + ").");
            if (aktuell.Pflicht) { pflichtFehler = true; Fertig(); return; }
        }
        NaechstePhase();
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
                if (int.TryParse(t[1], out p) && p >= 0)
                    SetzeProzent(aktuell.Von + (aktuell.Bis - aktuell.Von) * Math.Min(p, 100) / 100);
                if (t[2] != "Fertig")
                {
                    // Fortschritts-Updates desselben Schritts (z.B. "SFC (45 %)") ersetzen nur den Text
                    var letzter = liste.Items.Count > 0 ? (Schritt)liste.Items[liste.Items.Count - 1] : null;
                    string basis = System.Text.RegularExpressions.Regex.Replace(t[2], @"\s*\(\d{1,3} %\)$", "");
                    if (letzter != null && !letzter.Kopf && !letzter.Fertig &&
                        System.Text.RegularExpressions.Regex.Replace(letzter.Text, @"\s*\(\d{1,3} %\)$", "") == basis)
                    {
                        letzter.Text = t[2];
                        liste.Invalidate();
                    }
                    else
                    {
                        SchrittHinzufuegen(new Schritt { Text = t[2] });
                        log.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t[2]);
                    }
                    lSchritt.Text = t[2] + " …";
                }
            }
        }
        else if (Kontext.Verarbeite(zeile))
        {
            phaseErgebnis = true;
        }
        else
        {
            log.AppendLine((fehler ? "! " : "  ") + zeile);
        }
        if (details.Visible) { details.Text = log.ToString(); details.SelectionStart = details.TextLength; details.ScrollToCaret(); }
    }

    // ---------------- Ergebnis ----------------

    static string Zahlen(string[] r)
    {
        return "Kritisch " + r[3] + "  ·  Warnungen " + r[4] + "  ·  Hinweise " + r[5] + "  ·  OK " + r[6];
    }

    static Color Farbe(string klasse)
    {
        return klasse == "krit" ? Ui.Rot : klasse == "warn" ? Ui.Orange : Ui.Gruen;
    }

    Label Banner(string text, Color farbe)
    {
        var l = Ui.Label(ergebnis, text, 0, 0, 564, 44);
        l.Font = new Font("Segoe UI", 12f, FontStyle.Bold); l.ForeColor = Color.White; l.BackColor = farbe;
        l.TextAlign = ContentAlignment.MiddleLeft; l.Padding = new Padding(12, 0, 0, 0);
        return l;
    }

    void HashZeile(string titel, string hash, int y)
    {
        Ui.Label(ergebnis, titel, 0, y + 4, 112, 20).ForeColor = Ui.Grau;
        var t = new TextBox(); t.ReadOnly = true; t.Text = hash; t.SetBounds(112, y, 358, 26); t.Font = new Font("Consolas", 9f);
        ergebnis.Controls.Add(t);
        var b = Ui.Button(ergebnis, "Kopieren", 476, y - 1, 88, 28);
        b.Click += delegate { try { Clipboard.SetText(t.Text); } catch { } };
    }

    void ZeigeEinzelErgebnis(string[] r)
    {
        // r: ##R, klasse, gesamt, krit, warn, hinw, ok, ordner, zip, hash, bericht, zeitraum
        Banner(r[2], Farbe(r[1]));
        Ui.Label(ergebnis, Zahlen(r), 0, 54, 564, 22).Font = Ui.Fett;
        Ui.Label(ergebnis, "Gespeichert in:", 0, 90, 564, 20).ForeColor = Ui.Grau;
        Ui.Label(ergebnis, r[7] + "\n" + r[8], 0, 110, 564, 40);
        Ui.Label(ergebnis, "SHA256-Prüfsumme des ZIP-Archivs (als Nachweis notieren oder mitschicken):", 0, 160, 564, 20).ForeColor = Ui.Grau;
        HashZeile("SHA256:", r[9], 184);
        ordnerPfad = r[7];
        berichtPfad = r[10];
    }

    void ZeigeReparaturErgebnis()
    {
        string[] vorher = Kontext.Protokolle.Count > 0 ? Kontext.Protokolle[0] : null;
        string[] nachher = Kontext.Protokolle.Count > 1 ? Kontext.Protokolle[1] : null;
        string[] rep = Kontext.Reparatur;

        if (nachher != null) Banner("Nach der Reparatur: " + nachher[2], Farbe(nachher[1]));
        else Banner("Protokoll nach der Reparatur fehlt", Ui.Orange);

        Ui.Label(ergebnis, "Vorher:", 0, 52, 70, 20).ForeColor = Ui.Grau;
        Ui.Label(ergebnis, vorher != null ? Zahlen(vorher) : "–", 70, 52, 494, 20);
        Ui.Label(ergebnis, "Nachher:", 0, 74, 70, 20).ForeColor = Ui.Grau;
        Ui.Label(ergebnis, nachher != null ? Zahlen(nachher) : "–", 70, 74, 494, 20).Font = Ui.Fett;

        Ui.Label(ergebnis, "Reparatur:", 0, 100, 70, 20).ForeColor = Ui.Grau;
        var lRep = Ui.Label(ergebnis, rep != null ? rep[2] : "Kein Reparaturergebnis (siehe Details)", 70, 100, 494, 20);
        lRep.Font = Ui.Fett; lRep.ForeColor = rep != null ? Farbe(rep[1]) : Ui.Rot;

        int y = 126;
        if (rep != null && rep[3] == "1")
        {
            var n = Ui.Label(ergebnis, "⟳  Neustart erforderlich, damit alle Reparaturen wirksam werden. Danach am besten erneut ein Protokoll erstellen.", 0, y, 564, 36);
            n.ForeColor = Ui.Orange; n.Font = Ui.Fett; n.AutoEllipsis = false;
            y += 40;
        }
        else y += 6;

        Ui.Label(ergebnis, "Gespeichert in:", 0, y, 100, 20).ForeColor = Ui.Grau;
        Ui.Label(ergebnis, hauptordner, 100, y, 464, 20);
        y += 26;
        if (vorher != null) { HashZeile("SHA256 vorher:", vorher[9], y); y += 30; }
        if (nachher != null) { HashZeile("SHA256 nachher:", nachher[9], y); }

        ordnerPfad = hauptordner;
        berichtPfad = nachher != null ? nachher[10] : (rep != null ? rep[6] : (vorher != null ? vorher[10] : null));
    }

    void Fertig()
    {
        if (fertig) return;
        fertig = true;
        laeuft = false;
        uhr.Stop();
        AktualisiereZeit();
        if (abgebrochen) return;

        bAbbrechen.Visible = false;
        bSchliessen.Visible = true;
        AcceptButton = bSchliessen;

        bool ok = reparaturModus ? (!pflichtFehler && Kontext.Protokolle.Count > 0) : Kontext.Protokolle.Count > 0;
        if (ok)
        {
            ExitCode = 0;
            SetzeProzent(100);
            if (reparaturModus)
            {
                lTitel.Text = "Reparatur abgeschlossen";
                lSchritt.Text = "Protokoll vorher, Reparatur und Protokoll nachher sind fertig.";
                ZeigeReparaturErgebnis();
            }
            else
            {
                lTitel.Text = "Protokoll erstellt";
                lSchritt.Text = "Alle Prüfungen abgeschlossen.";
                ZeigeEinzelErgebnis(Kontext.Protokolle[0]);
            }
            if (liste.Items.Count > 0) ((Schritt)liste.Items[liste.Items.Count - 1]).Fertig = true;
            liste.Visible = false; ergebnis.Visible = true;
            bBericht.Visible = berichtPfad != null; bOrdner.Visible = true;
            bBericht.Focus();
            Refresh();
            Ui.Screenshot(this, reparaturModus ? "3-ergebnis-reparatur" : "3-ergebnis");
            Activate();
        }
        else
        {
            ExitCode = 1;
            lTitel.Text = pflichtFehler && reparaturModus ? "Protokoll vorher fehlgeschlagen – keine Reparatur durchgeführt" : "Protokoll konnte nicht erstellt werden";
            lTitel.ForeColor = Ui.Rot;
            lSchritt.Text = "Details siehe unten.";
            ZeigeDetails(true);
            Refresh();
            Ui.Screenshot(this, "3-fehler");
        }
        if (autoClose)
        {
            // Testlauf: Detail-Protokoll fuer die Auswertung ablegen
            TestLogSchreiben();
            var t = new System.Windows.Forms.Timer(); t.Interval = 1500;
            t.Tick += delegate { t.Stop(); Close(); };
            t.Start();
        }
    }

    void BeimSchliessen(object sender, FormClosingEventArgs e)
    {
        if (!laeuft || fertig) return;
        string frage = aktuell != null && aktuell.IstReparatur
            ? "Eine Reparatur läuft gerade.\nEin Abbruch kann Windows-Komponenten in einem unvollständigen Zustand hinterlassen.\n\nWirklich abbrechen?"
            : "Die Prüfung läuft noch. Wirklich abbrechen?\nEs wird kein vollständiges Protokoll erstellt.";
        if (!autoClose && MessageBox.Show(this, frage, "Abbrechen?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
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
