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

    [STAThread]
    static int Main(string[] argv)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool keinUpdate = false, nurVersion = false, updateTest = false, guiTest = false;
        bool still = false, schnell = false, zeitAngegeben = false, zielAngegeben = false;
        var args = new List<string>();
        foreach (string a in argv)
        {
            string l = a.ToLowerInvariant();
            if (l == "--kein-update") keinUpdate = true;
            else if (l == "--version") nurVersion = true;
            else if (l == "--update-test") updateTest = true;
            else if (l == "--gui-test") guiTest = true;
            else
            {
                if (l == "-still") still = true;
                if (l == "-schnell") schnell = true;
                if (l == "-ziel") zielAngegeben = true;
                if (Array.IndexOf(ZeitParameter, l) >= 0) zeitAngegeben = true;
                args.Add(a);
            }
        }

        string version = Updater.CurrentVersion.ToString(3);

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
        if (guiTest && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYSTEMPROTOKOLL_SCREENSHOTS")))
        {
            Ui.Vorschau(new StartDialog(false, @"C:\Users\Beispiel\Desktop"), "1-start");
            Updater.VorschauAskDialog();
        }

        // Im stillen Modus (z.B. geplante Aufgabe) nicht nach Updates fragen
        if (!keinUpdate && !still && !guiTest)
        {
            if (Updater.CheckAndOffer(argv)) return 0;
        }

        // Startdialog: Zeitraum und Speicherort
        if (!still && !guiTest && !zeitAngegeben)
        {
            string zielVorschlag = Ui.LadeEinstellung("speicherort", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            using (var d = new StartDialog(schnell, zielVorschlag))
            {
                if (d.ShowDialog() != DialogResult.OK) return 0;
                DateTime ende = d.Ende;
                if (ende.TimeOfDay == TimeSpan.Zero) ende = ende.AddSeconds(-1);   // sonst zaehlt das Skript den ganzen Tag
                args.Add("-Von"); args.Add(d.Start.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
                args.Add("-Bis"); args.Add(ende.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
                if (d.Schnell && !schnell) args.Add("-Schnell");
                if (!zielAngegeben) { args.Add("-Ziel"); args.Add(d.Ziel); }
                Ui.SpeichereEinstellung("speicherort", d.Ziel);
            }
        }

        string ordner = Path.Combine(Path.GetTempPath(), "Systemprotokoll_" + Guid.NewGuid().ToString("N"));
        string skript = Path.Combine(ordner, "Systemprotokoll.ps1");
        int code = 1;
        try
        {
            Directory.CreateDirectory(ordner);
            using (Stream quelle = Assembly.GetExecutingAssembly().GetManifestResourceStream("Systemprotokoll.ps1"))
            using (FileStream ziel = File.Create(skript))
            {
                if (quelle == null) throw new Exception("Eingebettetes Skript nicht gefunden.");
                quelle.CopyTo(ziel);
            }

            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";

            var info = new ProcessStartInfo(ps,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + skript + "\" " + Updater.QuoteArgs(args.ToArray()));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.EnvironmentVariables["SYSTEMPROTOKOLL_VERSION"] = version;

            if (still)
            {
                using (Process p = Process.Start(info)) { p.WaitForExit(); code = p.ExitCode; }
            }
            else
            {
                info.EnvironmentVariables["SYSTEMPROTOKOLL_GUI"] = "1";
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = new UTF8Encoding(false);
                info.StandardErrorEncoding = new UTF8Encoding(false);
                using (var f = new FortschrittForm(info, guiTest))
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
            try { Directory.Delete(ordner, true); } catch { }
        }
        return code;
    }

    /// Ausgabe fuer Kommandozeilen-Schalter: in Datei/Pipe, sonst als Meldung.
    static void Ausgabe(string text)
    {
        if (Console.IsOutputRedirected) { Console.WriteLine(text); Console.Out.Flush(); }
        else MessageBox.Show(text, "Systemzustands-Protokoll");
    }
}
