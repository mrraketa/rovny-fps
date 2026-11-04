// Katka.cs — замер во время игры («Замер в игре»): пока включённая игра запущена, PresentMon пишет её кадры в фоне,
// а раз в 250 мс отмечается, что происходит: игра впереди или нет, какое окно впереди и на каком мониторе,
// виден ли курсор (в бою он обычно спрятан, в меню виден), идут ли кадры через Windows (окно, оверлей).
// После выхода из игры — итог по состояниям, разбор по секундам и самые долгие кадры с причиной.
// Его слово 04.11.2026: «чтобы было понятно, от чего скачет FPS — альт-таб, другой монитор, меню», «максимум инфы».
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RovnyFps
{
    class Katka
    {
        // состояние в момент кадра
        public const byte VIgre = 0, Menu = 1, AltTab = 2, DrugoyMonitor = 3;
        static readonly string[] Imena = { "в игре", "курсор виден (меню, магазин, загрузка)", "альт-таб на тот же монитор", "окно впереди на другом мониторе" };

        readonly Game g;
        readonly Action<string> konec;   // итог для окна (вызывается из фонового потока)
        Thread potok, nablyudatel;
        Process pm;
        volatile bool stop;
        volatile byte sost; volatile string vperedi = ""; volatile int pidIgry;

        // кадры целиком (40 минут при 300 FPS — около 720 тыс., это ~4 МБ)
        readonly List<int> ft = new List<int>();          // мкс
        readonly List<byte> st = new List<byte>();        // состояние
        readonly List<bool> cherez = new List<bool>();    // через Windows
        readonly List<double> kogda = new List<double>(); // секунды от начала
        readonly List<string> kto = new List<string>();   // что было впереди в момент кадра (по секундам врало: 04.11 «альт-таб, впереди Deadlock»)
        readonly Dictionary<int, string> okno = new Dictionary<int, string>(); // секунда → что было впереди
        DateTime nachalo;

        public bool Idet { get { return potok != null && potok.IsAlive; } }
        public string Igra { get { return g.Name; } }

        Katka(Game g, Action<string> konec) { this.g = g; this.konec = konec; }

        public static Katka Nachat(Game g, Action<string> konec)
        {
            var k = new Katka(g, konec);
            k.potok = new Thread(k.Rabota) { IsBackground = true, Name = "katka" };
            k.potok.Start();
            return k;
        }

        public void Ostanovit() { stop = true; try { if (pm != null && !pm.HasExited) pm.Kill(); } catch (Exception) { } }

        // ---------- что происходит на экране ----------
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

        void Nablyudat()
        {
            IntPtr oknoIgry = IntPtr.Zero; DateTime iskal = DateTime.MinValue;
            while (!stop)
            {
                try
                {
                    int pi = pidIgry;
                    if (pi != 0 && (oknoIgry == IntPtr.Zero || (DateTime.UtcNow - iskal).TotalSeconds > 5))
                    {
                        iskal = DateTime.UtcNow;
                        try { oknoIgry = Process.GetProcessById(pi).MainWindowHandle; } catch (Exception) { }
                    }
                    var fg = GetForegroundWindow(); uint fp; GetWindowThreadProcessId(fg, out fp);
                    // пока PresentMon не назвал pid игры (первая секунда) — узнаём игру по имени процесса, иначе это сочлось бы альт-табом
                    bool igra = pi != 0 ? fp == pi : EtoIgra(fp);
                    if (igra)
                    {
                        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                        bool kursor = GetCursorInfo(ref ci) && (ci.flags & 1) != 0;
                        sost = kursor ? Menu : VIgre; vperedi = g.Name;
                    }
                    else
                    {
                        string imya = "?";
                        try { imya = Process.GetProcessById((int)fp).ProcessName; } catch (Exception) { }
                        bool drugoy = oknoIgry != IntPtr.Zero && MonitorFromWindow(fg, 2) != MonitorFromWindow(oknoIgry, 2);
                        sost = drugoy ? DrugoyMonitor : AltTab; vperedi = imya;
                    }
                }
                catch (Exception) { }
                Thread.Sleep(250);
            }
        }

        bool EtoIgra(uint pid)
        {
            try
            {
                string imya = Process.GetProcessById((int)pid).ProcessName;
                return g.Exes.Any(e => string.Equals(Path.GetFileNameWithoutExtension(e), imya, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception) { return false; }
        }

        // ---------- запись ----------
        void Rabota()
        {
            nachalo = DateTime.Now;
            Zhurnal.Pishi("замер в игре: начал, " + g.Name + " (" + string.Join(", ", g.Exes) + ")");
            string itog;
            try
            {
                nablyudatel = new Thread(Nablyudat) { IsBackground = true, Name = "katka-ekran" };
                nablyudatel.Start();
                var args = new StringBuilder();
                foreach (var e in g.Exes) args.Append("--process_name \"" + e + "\" ");
                // своё имя сессии: «Подобрать» и «Проверить» могут идти параллельно со своим
                args.Append("--output_stdout --no_console_stats --v2_metrics --session_name RovnyFpsIgra --stop_existing_session --terminate_on_proc_exit");
                pm = Process.Start(new ProcessStartInfo(Zamer.PmPath, args.ToString())
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                int oshibok = 0;
                pm.ErrorDataReceived += (s2, ev) => { if (ev.Data != null && oshibok++ < 30) Zhurnal.Pishi("замер в игре, PresentMon stderr: " + ev.Data); };
                pm.BeginErrorReadLine();
                Zhurnal.Pishi("замер в игре: PresentMon " + args);
                string[] head = null; int cPid = -1, cFt = -1, cMode = -1;
                string line; var ci = CultureInfo.InvariantCulture;
                while (!stop && (line = pm.StandardOutput.ReadLine()) != null)
                {
                    var f = line.Split(',');
                    if (head == null)
                    {
                        if (f.Length == 0 || f[0] != "Application") continue;
                        head = f;
                        cPid = Array.IndexOf(head, "ProcessID"); cMode = Array.IndexOf(head, "PresentMode");
                        cFt = Array.IndexOf(head, "MsBetweenPresents"); if (cFt < 0) cFt = Array.IndexOf(head, "FrameTime");
                        if (cPid < 0 || cFt < 0) throw new IOException("PresentMon выдал незнакомые столбцы: " + line);
                        continue;
                    }
                    int pid; double ms;
                    if (f.Length <= Math.Max(cFt, cPid) || !int.TryParse(f[cPid], out pid) || !double.TryParse(f[cFt], NumberStyles.Float, ci, out ms) || ms <= 0) continue;
                    pidIgry = pid;
                    double sek = (DateTime.Now - nachalo).TotalSeconds;
                    string v = vperedi;
                    ft.Add((int)(ms * 1000)); st.Add(sost); kogda.Add(sek); kto.Add(v);
                    cherez.Add(cMode >= 0 && cMode < f.Length && f[cMode].StartsWith("Composed"));
                    int s = (int)sek; if (!okno.ContainsKey(s)) okno[s] = v;
                }
                itog = Itog();
            }
            catch (Exception ex) { Zhurnal.Pishi("замер в игре — сбой: " + ex); itog = "Замер в игре не удался: " + ex.Message; }
            finally
            {
                stop = true;
                try { if (pm != null && !pm.HasExited) pm.Kill(); } catch (Exception) { }
                try
                {
                    var k = Process.Start(new ProcessStartInfo(Zamer.PmPath, "--terminate_existing_session --session_name RovnyFpsIgra") { UseShellExecute = false, CreateNoWindow = true });
                    k.WaitForExit(5000);
                }
                catch (Exception) { }
            }
            konec(itog);
        }

        // ---------- итог ----------
        static string Ch(double v) { return Itog_.Chislo(v); }

        string Itog()
        {
            double vsego = kogda.Count > 0 ? kogda[kogda.Count - 1] : 0;
            if (ft.Count < 100) { Zhurnal.Pishi("замер в игре: кадров мало (" + ft.Count + "), итога нет"); return null; }
            var sb = new StringBuilder();
            sb.AppendLine("Замер в игре: " + g.Name + ", " + nachalo.ToString("dd.MM.yyyy HH:mm") + ", длилось " + Min(vsego) + ", кадров " + ft.Count);
            sb.AppendLine("Ровный FPS версия " + Program.Versiya + ", экран " + Core.MonitorHz() + " Гц");
            sb.AppendLine();
            sb.AppendLine("ПО СОСТОЯНИЯМ (что было в момент кадра):");
            string vIgre = null;
            for (byte s = 0; s < Imena.Length; s++)
            {
                var k = new List<int>(); int cw = 0;
                for (int i = 0; i < ft.Count; i++) if (st[i] == s) { k.Add(ft[i]); if (cherez[i]) cw++; }
                if (k.Count == 0) continue;
                var it = Zamer.Poschitat(k);
                double sekund = k.Sum(x => (double)x) / 1e6;
                string str = Imena[s] + ": " + Min(sekund) + ", кадров " + k.Count +
                    (it != null ? ", средний " + Ch(it.Sredniy) + ", 1% low " + Ch(it.Low1) + ", 0,1% low " + Ch(it.Low01) + ", держит " + Ch(it.Derzhit) + ", рывков " + Ch(it.RyvkovVMin) + " в мин" : "") +
                    (cw > 0 ? ", через Windows " + (100 * cw / k.Count) + "% кадров" : "");
                sb.AppendLine("  " + str);
                if (s == VIgre && it != null)
                    vIgre = "в игре " + Min(sekund) + ": 1% low " + Ch(it.Low1) + ", рывков " + Ch(it.RyvkovVMin) + " в мин" + (cw * 2 > k.Count ? ", через Windows" : "");
            }
            // кто был впереди вместо игры
            var vmesto = okno.Values.Where(v => v != g.Name && v != "").GroupBy(v => v).OrderByDescending(x => x.Count()).Take(5).ToList();
            if (vmesto.Count > 0)
                sb.AppendLine("  впереди вместо игры: " + string.Join(", ", vmesto.Select(x => x.Key + " " + x.Count() + " с")));
            sb.AppendLine();
            // самые долгие кадры с причиной
            sb.AppendLine("САМЫЕ ДОЛГИЕ КАДРЫ:");
            foreach (var i in Enumerable.Range(0, ft.Count).OrderByDescending(i => ft[i]).Take(15))
            {
                sb.AppendLine("  " + nachalo.AddSeconds(kogda[i]).ToString("HH:mm:ss") + "  " + Ch(ft[i] / 1000.0) + " мс  — " + Imena[st[i]] +
                    (cherez[i] ? ", через Windows" : "") + (st[i] >= AltTab && !string.IsNullOrEmpty(kto[i]) ? ", впереди " + kto[i] : ""));
            }
            // по секундам — в CSV рядом
            string dir = Path.Combine(Path.GetDirectoryName(Zamer.PmPath), "zamery", "katki");
            Directory.CreateDirectory(dir);
            string imya = nachalo.ToString("yyyy-MM-dd_HH-mm") + "_" + string.Concat(g.Name.Split(Path.GetInvalidFileNameChars()));
            var csv = new StringBuilder("секунда;время;кадров;FPS;самый долгий мс;состояние;через Windows %;впереди\r\n");
            int j = 0;
            for (int s = 0; s <= (int)vsego; s++)
            {
                int n = 0, cw = 0, mx = 0; long sum = 0; var cnt = new int[Imena.Length];
                while (j < ft.Count && (int)kogda[j] == s) { n++; sum += ft[j]; mx = Math.Max(mx, ft[j]); cnt[st[j]]++; if (cherez[j]) cw++; j++; }
                if (n == 0) continue;
                int glavnoe = Array.IndexOf(cnt, cnt.Max());
                string ktoS; okno.TryGetValue(s, out ktoS);
                csv.Append(s + ";" + nachalo.AddSeconds(s).ToString("HH:mm:ss") + ";" + n + ";" + Ch(n * 1e6 / sum) + ";" + Ch(mx / 1000.0) + ";" + Imena[glavnoe] + ";" + (100 * cw / n) + ";" + ktoS + "\r\n");
            }
            File.WriteAllText(Path.Combine(dir, imya + ".csv"), csv.ToString(), new UTF8Encoding(true));   // с BOM — Excel откроет кириллицу
            sb.AppendLine();
            sb.AppendLine("По секундам: " + imya + ".csv");
            File.WriteAllText(Path.Combine(dir, imya + ".txt"), sb.ToString(), new UTF8Encoding(false));
            Zhurnal.Pishi("замер в игре — итог:\r\n" + sb.ToString().TrimEnd());

            var prochee = new List<string>();
            for (byte s = 1; s < Imena.Length; s++)
            {
                double sekund = 0; for (int i = 0; i < ft.Count; i++) if (st[i] == s) sekund += ft[i] / 1e6;
                if (sekund >= 30) prochee.Add((s == Menu ? "меню" : s == AltTab ? "альт-таб" : "другой монитор") + " " + Min(sekund));
            }
            return "Катка " + g.Name + " " + Min(vsego) + ": " + (vIgre ?? "в игре почти не был") + "." +
                (prochee.Count > 0 ? "\n" + string.Join(", ", prochee) + " — не в зачёт." : "") + " Подробно — «Журнал».";
        }

        static string Min(double sekund) { return sekund >= 90 ? Math.Round(sekund / 60) + " мин" : Math.Round(sekund) + " с"; }
    }

    // доступ к Itog.Chislo без путаницы с методом Katka.Itog()
    static class Itog_ { public static string Chislo(double v) { return RovnyFps.Itog.Chislo(v); } }
}
