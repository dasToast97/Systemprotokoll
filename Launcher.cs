// Starter fuer das Systemzustands-Protokoll.
// Enthaelt Systemprotokoll.ps1 als eingebettete Ressource, entpackt es in einen
// temporaeren Ordner und fuehrt es mit PowerShell aus (Adminrechte per Manifest).
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("Systemzustands-Protokoll")]
[assembly: AssemblyProduct("Systemzustands-Protokoll")]
[assembly: AssemblyVersion("1.0.0.0")]

class Programm
{
    static int Main(string[] args)
    {
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

            StringBuilder argumente = new StringBuilder();
            argumente.Append("-NoProfile -ExecutionPolicy Bypass -File \"").Append(skript).Append("\"");
            foreach (string a in args) argumente.Append(" \"").Append(a.Replace("\"", "")).Append("\"");

            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";

            ProcessStartInfo info = new ProcessStartInfo(ps, argumente.ToString());
            info.UseShellExecute = false;
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
