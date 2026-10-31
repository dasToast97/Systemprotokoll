// Starter fuer das Systemzustands-Protokoll.
// Enthaelt Systemprotokoll.ps1 als eingebettete Ressource, prueft auf Updates,
// zeigt Startdialog (Zeitraum + Speicherort) und Fortschrittsfenster und fuehrt
// das Skript unsichtbar mit PowerShell aus (Adminrechte per Manifest).
//
// Zusaetzliche Schalter der EXE (werden nicht an das Skript weitergegeben):
//   --kein-update   Update-Pruefung ueberspringen
//   --version       Version anzeigen und beenden
//   --update-test   Nur pruefen, welche Version auf GitHub verfuegbar ist (fuer Tests)
//   --gui-test      Fortschrittsfenster ohne Rueckfragen, schliesst sich am Ende selbst (fuer Tests)
//
// Reparatur per Kommandozeile: -Reparatur "System,Datentraeger,Treiber" [-Wiederherstellungspunkt]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("Systemzustands-Protokoll")]
[assembly: AssemblyProduct("Systemzustands-Protokoll")]
[assembly: AssemblyVersion(BuildInfo.Version)]
[assembly: AssemblyFileVersion(BuildInfo.Version)]

class Programm
{
    static readonly string[] ZeitParameter = { "-von", "-bis", "-dauer", "-tage", "-ohnedialog" };
    static string version;
    static string tempOrdner;

    [STAThread]
    static int Main(string[] argv)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool keinUpdate = false, nurVersion = false, updateTest = false, guiTest = false;
        bool still = false, schnell = false, zeitAngegeben = false, wiederherstellung = false;
        string ziel = null, reparaturen = null;
        var args = new List<string>();   // Parameter fuer das Protokoll-Skript (ohne -Ziel/-Reparatur)
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            string l = a.ToLowerInvariant();
            if (l == "--kein-update") keinUpdate = true;
            else if (l == "--version") nurVersion = true;
            else if (l == "--update-test") updateTest = true;
            else if (l == "--gui-test") guiTest = true;
            else if (l == "-ziel" && i + 1 < argv.Length) ziel = argv[++i];
            else if (l == "-reparatur" && i + 1 < argv.Length) reparaturen = argv[++i];
            else if (l == "-wiederherstellungspunkt") wiederherstellung = true;
            else
            {
                if (l == "-still") still = true;
                if (l == "-schnell") schnell = true;
                if (Array.IndexOf(ZeitParameter, l) >= 0) zeitAngegeben = true;
                args.Add(a);
            }
        }

        version = Updater.CurrentVersion.ToString(3);

        if (nurVersion) { Ausgabe(version); return 0; }

        if (updateTest)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                var rel = Updater.GetLatest(15000);
                if (rel == null) { Ausgabe("Kein gueltiges Release gefunden."); return 2; }
                Ausgabe("Installiert: " + version + " | Verfuegbar: " + rel.Version.ToString(3) +
                        " | " + rel.DownloadUrl + " | SHA256: " + (rel.Sha256 ?? "-"));
                return 0;
            }
            catch (Exception ex) { Ausgabe("Fehler: " + ex.Message); return 1; }
        }

        Updater.CleanupOld();

        // Nur fuer Tests: Bilder der Fenster erzeugen
        if (guiTest && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYSTEMPROTOKOLL_SCREENSHOTS")) && reparaturen == null)
        {
            Ui.Vorschau(new StartDialog(false, @"C:\Users\Beispiel\Desktop"), "1-start");
            Ui.Vorschau(new StartDialog(false, @"C:\Users\Beispiel\Desktop", true), "1-start-reparatur");
            Updater.VorschauAskDialog();
        }

        // Im stillen Modus (z.B. geplante Aufgabe) nicht nach Updates fragen
        if (!keinUpdate && !still && !guiTest)
        {
            if (Updater.CheckAndOffer(argv)) return 0;
        }

        // Startdialog: Modus, Reparaturen, Zeitraum und Speicherort
        if (!still && !guiTest && !zeitAngegeben)
        {
            string zielVorschlag = ziel ?? Ui.LadeEinstellung("speicherort", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            using (var d = new StartDialog(schnell, zielVorschlag, reparaturen != null))
            {
                if (d.ShowDialog() != DialogResult.OK) return 0;
                DateTime ende = d.Ende;
                if (ende.TimeOfDay == TimeSpan.Zero) ende = ende.AddSeconds(-1);   // sonst zaehlt das Skript den ganzen Tag
                args.Add("-Von"); args.Add(Datum(d.Start));
                args.Add("-Bis"); args.Add(Datum(ende));
                if (d.Schnell && !schnell) { args.Add("-Schnell"); schnell = true; }
                ziel = d.Ziel;
                Ui.SpeichereEinstellung("speicherort", d.Ziel);
                if (d.Reparieren) { reparaturen = d.Reparaturen; wiederherstellung = d.Wiederherstellungspunkt; }
                else reparaturen = null;
            }
        }
        if (string.IsNullOrEmpty(ziel)) ziel = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        tempOrdner = Path.Combine(Path.GetTempPath(), "Systemprotokoll_" + Guid.NewGuid().ToString("N"));
        int code = 1;
        try
        {
            Directory.CreateDirectory(tempOrdner);
            string protokollSkript = Entpacke("Systemprotokoll.ps1");
            string reparaturSkript = Entpacke("Reparatur.ps1");

            var phasen = new List<Phase>();
            string hauptordner = ziel;

            if (reparaturen == null)
            {
                var a1 = new List<string>(args); a1.Add("-Ziel"); a1.Add(ziel);
                phasen.Add(new Phase { Titel = "Protokoll", Pflicht = true, Von = 0, Bis = 100,
                                       Erzeuge = k => Psi(protokollSkript, a1, !still) });
            }
            else
            {
                hauptordner = Path.Combine(ziel, "Reparatur_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(hauptordner);
                string haupt = hauptordner;
                bool sys = ("," + reparaturen.ToLowerInvariant() + ",").Contains(",system,");

                var vorherArgs = new List<string>(args);
                vorherArgs.AddRange(new[] { "-Ziel", haupt, "-Bezeichnung", "Vorher" });
                phasen.Add(new Phase { Titel = "Protokoll vor der Reparatur", Pflicht = true, Von = 0, Bis = sys ? 25 : 35,
                                       Erzeuge = k => Psi(protokollSkript, vorherArgs, !still) });

                var repArgs = new List<string> { "-Ziel", haupt, "-Reparatur", reparaturen };
                if (wiederherstellung) repArgs.Add("-Wiederherstellungspunkt");
                phasen.Add(new Phase { Titel = "Reparatur", IstReparatur = true, Von = sys ? 25 : 35, Bis = sys ? 80 : 65,
                                       Erzeuge = k => { k.ReparaturStart = DateTime.Now; return Psi(reparaturSkript, repArgs, !still); } });

                bool schnellNachher = schnell;
                phasen.Add(new Phase { Titel = "Protokoll nach der Reparatur", Von = sys ? 80 : 65, Bis = 100,
                                       Erzeuge = k =>
                                       {
                                           var n = new List<string> { "-Von", Datum(k.ReparaturStart.AddMinutes(-1)), "-Bis", Datum(DateTime.Now),
                                                                      "-Ziel", haupt, "-Bezeichnung", "Nachher" };
                                           if (schnellNachher) n.Add("-Schnell");
                                           if (k.Protokolle.Count > 0) { n.Add("-Vergleich"); n.Add(Path.Combine(k.Protokolle[0][7], "Befunde.json")); }
                                           if (k.Reparatur != null) { n.Add("-ReparaturLog"); n.Add(k.Reparatur[5]); }
                                           return Psi(protokollSkript, n, !still);
                                       } });
            }

            if (still) code = StillAusfuehren(phasen);
            else
            {
                using (var f = new FortschrittForm(phasen, guiTest, hauptordner))
                {
                    Application.Run(f);
                    code = f.ExitCode;
                }
            }
        }
        catch (Exception ex)
        {
            if (still) Ausgabe("Fehler: " + ex.Message);
            else MessageBox.Show("Fehler: " + ex.Message, "Systemzustands-Protokoll", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { Directory.Delete(tempOrdner, true); } catch { }
        }
        return code;
    }

    static string Datum(DateTime d) { return d.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture); }

    static string Entpacke(string name)
    {
        string pfad = Path.Combine(tempOrdner, name);
        using (Stream quelle = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (quelle == null) throw new Exception("Eingebettetes Skript nicht gefunden: " + name);
            using (FileStream z = File.Create(pfad)) quelle.CopyTo(z);
        }
        return pfad;
    }

    /// Startinfo fuer ein Skript; die Ausgabe wird immer mitgelesen (Fortschritts- und Ergebniszeilen).
    static ProcessStartInfo Psi(string skript, List<string> skriptArgs, bool gui)
    {
        string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";
        var info = new ProcessStartInfo(ps,
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + skript + "\" " + Updater.QuoteArgs(skriptArgs.ToArray()));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.EnvironmentVariables["SYSTEMPROTOKOLL_VERSION"] = version;
        info.EnvironmentVariables["SYSTEMPROTOKOLL_GUI"] = "1";   // maschinenlesbare Fortschritts-/Ergebniszeilen
        if (!gui) info.EnvironmentVariables["SYSTEMPROTOKOLL_STILL"] = "1";
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        return info;
    }

    /// Ohne Fenster: Phasen nacheinander ausfuehren (z.B. geplante Aufgabe).
    static int StillAusfuehren(List<Phase> phasen)
    {
        var k = new PhasenKontext();
        foreach (var ph in phasen)
        {
            bool ergebnis = false;
            using (var p = new Process())
            {
                p.StartInfo = ph.Erzeuge(k);
                p.OutputDataReceived += (s, e) => { if (e.Data != null && k.Verarbeite(e.Data)) ergebnis = true; };
                p.ErrorDataReceived += (s, e) => { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
            }
            if (!ergebnis && ph.Pflicht) return 1;
        }
        return k.Protokolle.Count > 0 ? 0 : 1;
    }

    /// Ausgabe fuer Kommandozeilen-Schalter: in Datei/Pipe, sonst als Meldung.
    static void Ausgabe(string text)
    {
        if (Console.IsOutputRedirected) { Console.WriteLine(text); Console.Out.Flush(); }
        else MessageBox.Show(text, "Systemzustands-Protokoll");
    }
}
