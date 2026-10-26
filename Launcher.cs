// Starter fuer das Systemzustands-Protokoll.
// Enthaelt Systemprotokoll.ps1 als eingebettete Ressource, prueft auf Updates,
// entpackt das Skript in einen temporaeren Ordner und fuehrt es mit PowerShell aus
// (Adminrechte per Manifest).
//
// Zusaetzliche Schalter der EXE (werden nicht an das Skript weitergegeben):
//   --kein-update   Update-Pruefung ueberspringen
//   --version       Version anzeigen und beenden
//   --update-test   Nur pruefen, welche Version auf GitHub verfuegbar ist (fuer Tests)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("Systemzustands-Protokoll")]
[assembly: AssemblyProduct("Systemzustands-Protokoll")]
[assembly: AssemblyVersion(BuildInfo.Version)]
[assembly: AssemblyFileVersion(BuildInfo.Version)]

class Programm
{
    [STAThread]
    static int Main(string[] argv)
    {
        bool keinUpdate = false, nurVersion = false, updateTest = false, still = false;
        var args = new List<string>();
        foreach (string a in argv)
        {
            string l = a.ToLowerInvariant();
            if (l == "--kein-update") keinUpdate = true;
            else if (l == "--version") nurVersion = true;
            else if (l == "--update-test") updateTest = true;
            else
            {
                if (l == "-still") still = true;
                args.Add(a);
            }
        }

        string version = Updater.CurrentVersion.ToString(3);
        try { Console.Title = "Systemzustands-Protokoll " + version; } catch { }

        if (nurVersion) { Console.WriteLine(version); return 0; }

        if (updateTest)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                var rel = Updater.GetLatest(15000);
                if (rel == null) { Console.WriteLine("Kein gueltiges Release gefunden."); return 2; }
                Console.WriteLine("Installiert: " + version + " | Verfuegbar: " + rel.Version.ToString(3) +
                                  " | " + rel.DownloadUrl + " | SHA256: " + (rel.Sha256 ?? "-"));
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("Fehler: " + ex.Message); return 1; }
        }

        Updater.CleanupOld();

        // Im stillen Modus (z.B. geplante Aufgabe) nicht nach Updates fragen
        if (!keinUpdate && !still)
        {
            if (Updater.CheckAndOffer(args.ToArray())) return 0;
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

            var argumente = new StringBuilder();
            argumente.Append("-NoProfile -ExecutionPolicy Bypass -File \"").Append(skript).Append("\" ");
            argumente.Append(Updater.QuoteArgs(args.ToArray()));

            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";

            var info = new ProcessStartInfo(ps, argumente.ToString());
            info.UseShellExecute = false;
            info.EnvironmentVariables["SYSTEMPROTOKOLL_VERSION"] = version;
            using (Process p = Process.Start(info))
            {
                p.WaitForExit();
                code = p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fehler: " + ex.Message);
            Console.WriteLine("Enter druecken zum Beenden");
            Console.ReadLine();
        }
        finally
        {
            try { Directory.Delete(ordner, true); } catch { }
        }
        return code;
    }
}
