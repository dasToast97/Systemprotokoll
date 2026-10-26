// Auto-Update fuer Systemprotokoll.exe ueber GitHub Releases.
// Prueft beim Start auf eine neuere Version, fragt nach und ersetzt die EXE
// (laufende EXE wird umbenannt, neue an ihre Stelle gelegt und neu gestartet).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Updater
{
    const string AssetName = "Systemprotokoll.exe";

    public static Version CurrentVersion
    {
        get { Version v; return Version.TryParse(BuildInfo.Version, out v) ? v : new Version(0, 0, 0); }
    }

    static string SettingsDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Systemprotokoll"); }
    }
    static string SkipFile { get { return Path.Combine(SettingsDir, "skip-version.txt"); } }

    public class ReleaseInfo
    {
        public Version Version;
        public string Tag, Notes, DownloadUrl, PageUrl, Sha256;
        public long Size;
    }

    static HttpWebRequest Request(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Systemprotokoll/" + CurrentVersion.ToString(3);
        req.Accept = "application/vnd.github+json";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.AllowAutoRedirect = true;
        return req;
    }

    public static ReleaseInfo GetLatest(int timeoutMs)
    {
        if (string.IsNullOrEmpty(BuildInfo.Repo)) return null;
        string json;
        using (var resp = (HttpWebResponse)Request("https://api.github.com/repos/" + BuildInfo.Repo + "/releases/latest", timeoutMs).GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream()))
            json = sr.ReadToEnd();

        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        string tag = Convert.ToString(root["tag_name"]);
        Version ver;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out ver)) return null;

        var info = new ReleaseInfo();
        info.Version = ver;
        info.Tag = tag;
        info.Notes = root.ContainsKey("body") ? Convert.ToString(root["body"]) : "";
        info.PageUrl = root.ContainsKey("html_url") ? Convert.ToString(root["html_url"]) : "";

        foreach (object o in (IEnumerable)root["assets"])
        {
            var a = (Dictionary<string, object>)o;
            if (string.Equals(Convert.ToString(a["name"]), AssetName, StringComparison.OrdinalIgnoreCase))
            {
                info.DownloadUrl = Convert.ToString(a["browser_download_url"]);
                info.Size = Convert.ToInt64(a["size"]);
            }
        }
        if (info.DownloadUrl == null) return null;
        // Nur Downloads aus genau diesem Repository akzeptieren
        if (!info.DownloadUrl.StartsWith("https://github.com/" + BuildInfo.Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            return null;

        Match m = Regex.Match(info.Notes ?? "", @"\b[0-9A-Fa-f]{64}\b");
        if (m.Success) info.Sha256 = m.Value.ToUpperInvariant();
        return info;
    }

    static bool IsNewer(Version remote)
    {
        var c = CurrentVersion;
        return new Version(remote.Major, remote.Minor, Math.Max(remote.Build, 0)) >
               new Version(c.Major, c.Minor, Math.Max(c.Build, 0));
    }

    static string ReadSkipped()
    {
        try { return File.Exists(SkipFile) ? File.ReadAllText(SkipFile).Trim() : null; } catch { return null; }
    }

    static void WriteSkipped(string tag)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(SkipFile, tag); } catch { }
    }

    /// Loescht die alte EXE, die nach einem Update uebrig geblieben ist.
    public static void CleanupOld()
    {
        string old = Application.ExecutablePath + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); } catch { System.Threading.Thread.Sleep(300); }
        }
    }

    /// Prueft auf ein Update. Rueckgabe true = neue Version wurde gestartet, dieses Programm soll sich beenden.
    public static bool CheckAndOffer(string[] args)
    {
        if (string.IsNullOrEmpty(BuildInfo.Repo)) return false;
        Console.WriteLine("Suche nach Updates ...");
        ReleaseInfo rel;
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            rel = GetLatest(6000);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Update-Pruefung nicht moeglich (" + ex.Message + ") - fahre fort.");
            return false;
        }

        if (rel == null || !IsNewer(rel.Version))
        {
            Console.WriteLine("  Aktuelle Version " + CurrentVersion.ToString(3) + " ist die neueste.");
            return false;
        }
        if (ReadSkipped() == rel.Tag)
        {
            Console.WriteLine("  Version " + rel.Version.ToString(3) + " wurde uebersprungen.");
            return false;
        }

        switch (AskDialog(rel))
        {
            case DialogResult.Yes: return Install(rel, args);
            case DialogResult.Ignore: WriteSkipped(rel.Tag); return false;
            default: return false;
        }
    }

    static string Plain(string md)
    {
        string s = (md ?? "").Replace("\r", "");
        s = Regex.Replace(s, @"^#+\s*", "", RegexOptions.Multiline);
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
        return s.Trim().Replace("\n", "\r\n");
    }

    static DialogResult AskDialog(ReleaseInfo rel)
    {
        Application.EnableVisualStyles();
        using (var f = new Form())
        {
            f.Text = "Update verfügbar";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.ClientSize = new Size(480, 300);
            f.Font = new Font("Segoe UI", 9.5f);

            var head = new Label();
            head.Text = "Version " + rel.Version.ToString(3) + " ist verfügbar";
            head.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            head.SetBounds(16, 12, 450, 28);
            f.Controls.Add(head);

            var sub = new Label();
            sub.Text = "Installiert: " + CurrentVersion.ToString(3);
            sub.SetBounds(16, 42, 450, 20);
            f.Controls.Add(sub);

            var notes = new TextBox();
            notes.Multiline = true; notes.ReadOnly = true; notes.ScrollBars = ScrollBars.Vertical;
            notes.Text = Plain(rel.Notes);
            notes.SetBounds(16, 68, 448, 176);
            f.Controls.Add(notes);

            var bYes = new Button(); bYes.Text = "Jetzt aktualisieren"; bYes.DialogResult = DialogResult.Yes;
            bYes.SetBounds(16, 256, 150, 32); f.Controls.Add(bYes);
            var bLater = new Button(); bLater.Text = "Später"; bLater.DialogResult = DialogResult.No;
            bLater.SetBounds(174, 256, 90, 32); f.Controls.Add(bLater);
            var bSkip = new Button(); bSkip.Text = "Diese Version überspringen"; bSkip.DialogResult = DialogResult.Ignore;
            bSkip.SetBounds(272, 256, 192, 32); f.Controls.Add(bSkip);

            f.AcceptButton = bYes; f.CancelButton = bLater;
            f.Shown += delegate { bYes.Focus(); };
            return f.ShowDialog();
        }
    }

    static bool Install(ReleaseInfo rel, string[] args)
    {
        string exe = Application.ExecutablePath;
        string neu = exe + ".neu";
        try
        {
            Console.WriteLine("Lade Version " + rel.Version.ToString(3) + " herunter ...");
            var req = Request(rel.DownloadUrl, 60000);
            req.Accept = "application/octet-stream";
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(neu))
            {
                var buf = new byte[81920];
                long done = 0; int n; int lastPct = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (rel.Size > 0)
                    {
                        int pct = (int)(done * 100 / rel.Size);
                        if (pct != lastPct && pct % 10 == 0) { Console.Write("\r  " + pct + " %   "); lastPct = pct; }
                    }
                }
            }
            Console.WriteLine();

            var fi = new FileInfo(neu);
            if (fi.Length < 10000 || (rel.Size > 0 && fi.Length != rel.Size))
                throw new InvalidDataException("Die heruntergeladene Datei ist unvollständig.");

            if (rel.Sha256 != null)
            {
                string hash;
                using (var sha = SHA256.Create())
                using (var s = File.OpenRead(neu))
                    hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
                if (!string.Equals(hash, rel.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Prüfsumme stimmt nicht überein - Update verworfen.");
                Console.WriteLine("  Prüfsumme OK.");
            }

            // Laufende EXE kann umbenannt (aber nicht ueberschrieben) werden
            string old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(neu, exe); }
            catch { File.Move(old, exe); throw; }

            var psi = new ProcessStartInfo(exe, QuoteArgs(args));
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi);
            Console.WriteLine("Update installiert - neue Version wird gestartet.");
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(neu)) File.Delete(neu); } catch { }
            MessageBox.Show("Update fehlgeschlagen:\n" + ex.Message + "\n\nDas Protokoll wird mit der aktuellen Version erstellt.",
                "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public static string QuoteArgs(string[] args)
    {
        var parts = new List<string>();
        foreach (string a in args) parts.Add("\"" + a.Replace("\"", "") + "\"");
        return string.Join(" ", parts.ToArray());
    }
}
