// Zamer.cs — общее для замера: журнал, подсчёт FPS / 1% low / рывков, PresentMon.
// Подсчёт и PresentMon — те же, что в «Ровном FPS» (rovny-fps\Zamer.cs), чтобы цифры двух программ совпадали.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FpsMaster
{
    class Game
    {
        public string Name;
        public List<string> Exes = new List<string>();
        public bool Svoya;   // добавлена кнопкой «+ игра»
    }

    class Itog
    {
        public int Kadrov;
        public double Sredniy, Low1, Low01, RyvkovVMin, Derzhit;
        public static string Chislo(double v) { return v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.GetCultureInfo("ru-RU")); }
    }

    // Итог одной катки в цифрах (только секунды «в игре» — без меню и альт-таба)
    class KatkaItog
    {
        public string Igra, Prichiny = "", Vperedi = "", ExePut = "";
        public DateTime Kogda;
        public double SekundVIgre, Sredniy, Low1, Ryvki, Derzhit;
        public int CherezProc, Podvisaniy, MinSvobodnoMb = -1, VsegoMb = -1;
        public int UporGpu = -1;   // % кадров в бою, где не успевала видеокарта; -1 — нет данных

        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        static string E(string s) { return (s ?? "").Replace("\t", " ").Replace("\n", " "); }

        public string VStroku()
        {
            return string.Join("\t", new[] { "katka", E(Igra), Kogda.Ticks.ToString(), SekundVIgre.ToString("0.#", Ci), Sredniy.ToString("0.##", Ci),
                Low1.ToString("0.##", Ci), Ryvki.ToString("0.##", Ci), Derzhit.ToString("0.##", Ci), CherezProc.ToString(), Podvisaniy.ToString(),
                MinSvobodnoMb.ToString(), VsegoMb.ToString(), E(ExePut), E(Prichiny), E(Vperedi), UporGpu.ToString() });
        }

        public static KatkaItog IzStroki(string[] p)
        {
            if (p.Length < 15 || p[0] != "katka") return null;
            return new KatkaItog { Igra = p[1], Kogda = new DateTime(long.Parse(p[2])), SekundVIgre = double.Parse(p[3], Ci), Sredniy = double.Parse(p[4], Ci),
                Low1 = double.Parse(p[5], Ci), Ryvki = double.Parse(p[6], Ci), Derzhit = double.Parse(p[7], Ci), CherezProc = int.Parse(p[8]),
                Podvisaniy = int.Parse(p[9]), MinSvobodnoMb = int.Parse(p[10]), VsegoMb = int.Parse(p[11]), ExePut = p[12], Prichiny = p[13], Vperedi = p[14],
                UporGpu = p.Length > 15 ? int.Parse(p[15]) : -1 };
        }

        // «31 чтение с диска|1 заминка…» → пары (сколько, что)
        public List<KeyValuePair<int, string>> PoPrichinam()
        {
            var r = new List<KeyValuePair<int, string>>();
            foreach (var x in (Prichiny ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = x.IndexOf(' '); int n;
                if (sp > 0 && int.TryParse(x.Substring(0, sp), out n)) r.Add(new KeyValuePair<int, string>(n, x.Substring(sp + 1)));
            }
            return r;
        }

        // «explorer 12|steamwebhelper 10» → пары (что, секунд)
        public List<KeyValuePair<string, int>> KtoVperedi()
        {
            var r = new List<KeyValuePair<string, int>>();
            foreach (var x in (Vperedi ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = x.LastIndexOf(' '); int n;
                if (sp > 0 && int.TryParse(x.Substring(sp + 1), out n)) r.Add(new KeyValuePair<string, int>(x.Substring(0, sp), n));
            }
            return r;
        }
    }

    // Журнал шагов. Без секретов.
    static class Zhurnal
    {
        public static string Papka { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsMaster"); } }
        public static string Put { get { return Path.Combine(Papka, "zhurnal.txt"); } }
        static readonly object zamok = new object();
        public static void Pishi(string s)
        {
            try
            {
                lock (zamok)
                {
                    Directory.CreateDirectory(Papka);
                    var fi = new FileInfo(Put);
                    if (fi.Exists && fi.Length > 2000000) { File.Copy(Put, Path.ChangeExtension(Put, ".old.txt"), true); File.Delete(Put); }
                    File.AppendAllText(Put, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n", new UTF8Encoding(false));
                }
            }
            catch (Exception) { }   // журнал не должен ронять программу
        }
    }

    static class Zamer
    {
        // PresentMon (Intel, MIT) — та же версия и сумма, что в «Ровном FPS»
        public const string PmUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        public const string PmSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
        public static string PmPath { get { return Path.Combine(Zhurnal.Papka, "PresentMon-2.6.0-x64.exe"); } }
        static string PmRovny { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RovnyFPS", "PresentMon-2.6.0-x64.exe"); } }
        public static bool PmEst { get { return File.Exists(PmPath); } }

        static string Sha(string put)
        {
            using (var f = File.OpenRead(put)) using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // Если «Ровный FPS» уже скачал PresentMon — берём его копию (сумма та же), качать не надо
        public static bool VzyatURovnogo()
        {
            try
            {
                if (PmEst || !File.Exists(PmRovny) || Sha(PmRovny) != PmSha256) return PmEst;
                Directory.CreateDirectory(Zhurnal.Papka);
                File.Copy(PmRovny, PmPath, true);
                Zhurnal.Pishi("PresentMon взят у «Ровного FPS»");
                return true;
            }
            catch (Exception ex) { Zhurnal.Pishi("PresentMon у «Ровного FPS» взять не вышло: " + ex.Message); return false; }
        }

        public static void SkachatPm()
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            Directory.CreateDirectory(Zhurnal.Papka);
            string tmp = PmPath + ".new";
            using (var wc = new System.Net.WebClient()) wc.DownloadFile(PmUrl, tmp);
            if (Sha(tmp) != PmSha256) { File.Delete(tmp); throw new IOException("PresentMon скачался битым — попробуй ещё раз."); }
            if (File.Exists(PmPath)) File.Delete(PmPath);
            File.Move(tmp, PmPath);
        }

        // Время кадров в мкс → средний FPS, 1% и 0,1% low (как P1/P0.1 в CapFrameX), рывки в минуту — как в «Ровном FPS»
        public static Itog Poschitat(List<int> kadry)
        {
            if (kadry.Count < 100) return null;
            var s = kadry.OrderBy(x => x).ToList();
            double sum = kadry.Sum(x => (double)x), med = s[s.Count / 2];
            Func<double, double> low = share => 1e6 / s[Math.Min(s.Count - 1, (int)(s.Count * (1 - share)))];
            int ryvki = kadry.Count(x => x > 2 * med);
            var okna = new List<double>(); long t = 0; int n = 0;
            foreach (var k in kadry)
            {
                t += k; n++;
                if (t >= 500000) { okna.Add(n * 1e6 / t); t = 0; n = 0; }
            }
            okna.Sort();
            double derzhit = okna.Count > 0 ? okna[okna.Count / 20] : 1e6 * kadry.Count / sum;
            return new Itog { Kadrov = kadry.Count, Sredniy = 1e6 * kadry.Count / sum, Low1 = low(0.01), Low01 = low(0.001), RyvkovVMin = ryvki / (sum / 6e7), Derzhit = derzhit };
        }
    }
}
