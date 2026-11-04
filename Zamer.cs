// Zamer.cs — замер кадров из общей памяти RivaTuner и подбор лимита по нему.
// RivaTuner (v2.5+) хранит время каждого из последних 1024 кадров приложения в кольцевом буфере;
// смещения — по SDK\Include\RTSSSharedMemory.h (RTSS_SHARED_MEMORY_APP_ENTRY).
// Проверено 04.11.2026 в Deadlock тем же способом из zamer\rtss_fps.py.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RovnyFps
{
    class Itog
    {
        public int Kadrov;
        public double Sredniy, Low1, Low01, RyvkovVMin;
        public int Poverh;
        public bool CherezWindows;    // игра всё время выводила кадры через Windows (оконный режим или оверлей)            // секунд, когда поверх игры было окно или оверлей — такие кадры не считались
        public double Derzhit;        // FPS, который игра держит постоянно: 5-й процентиль по полусекундам
        public bool Tochno;           // true — PresentMon; false — буфер RivaTuner (под своим лимитом ровнее, чем есть)
        public static string Chislo(double v) { return v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")); }
    }

    // Журнал для разбора чужих сбоев (04.11: «у друга не запускаются замеры»): что программа делала по шагам.
    // %LOCALAPPDATA%\RovnyFPS\zhurnal.txt, больше 2 МБ — старый уходит в zhurnal.old.txt. Ссылка «Журнал» в окне его показывает.
    static class Zhurnal
    {
        public static string Put { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RovnyFPS", "zhurnal.txt"); } }
        static readonly object zamok = new object();
        public static void Pishi(string s)
        {
            try
            {
                lock (zamok)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Put));
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
        const int BufOff = 924, PosOff = 5020;
        const uint Signature = 0x52545353;   // 'RTSS'

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr n);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        static int ForegroundPid()
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return (int)pid;
        }

        // Запись игры в общей памяти: адрес начала и pid; IntPtr.Zero — RivaTuner игру не видит
        static IntPtr Find(IntPtr p, ICollection<string> exes, out int pid)
        {
            pid = 0;
            if ((uint)Marshal.ReadInt32(p) != Signature) return IntPtr.Zero;
            int esz = Marshal.ReadInt32(p, 8), aoff = Marshal.ReadInt32(p, 12), acnt = Marshal.ReadInt32(p, 16);
            var name = new byte[260];
            for (int i = 0; i < acnt; i++)
            {
                var e = p + aoff + i * esz;
                int id = Marshal.ReadInt32(e);
                if (id == 0) continue;
                Marshal.Copy(e + 4, name, 0, 260);
                string full = Encoding.Default.GetString(name).Split('\0')[0];
                string file;
                try { file = Path.GetFileName(full); } catch (ArgumentException) { continue; }
                if (exes.Any(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase))) { pid = id; return e; }
            }
            return IntPtr.Zero;
        }

        static string Vidit(IntPtr p)
        {
            try
            {
                int esz = Marshal.ReadInt32(p, 8), aoff = Marshal.ReadInt32(p, 12), acnt = Marshal.ReadInt32(p, 16);
                var names = new List<string>(); var name = new byte[260];
                for (int i = 0; i < acnt; i++)
                {
                    var e = p + aoff + i * esz;
                    if (Marshal.ReadInt32(e) == 0) continue;
                    Marshal.Copy(e + 4, name, 0, 260);
                    names.Add(Path.GetFileName(Encoding.Default.GetString(name).Split('\0')[0]));
                }
                return names.Count == 0 ? "ничего" : string.Join(", ", names);
            }
            catch (Exception ex) { return "ошибка чтения: " + ex.Message; }
        }

        // Ждёт, пока RivaTuner увидит игру, и собирает время кадров, пока окно игры впереди
        // (в фоне игры сами себя тормозят). progress(сколько секунд набрано).
        public static List<int> Sobrat(ICollection<string> exes, int sekund, Action<int> progress, Func<bool> zhdu, CancellationToken stop)
        {
            IntPtr h = IntPtr.Zero, p = IntPtr.Zero;
            var kadry = new List<int>();
            try
            {
                IntPtr e = IntPtr.Zero; int pid = 0;
                var zhdal = DateTime.UtcNow.AddSeconds(-20);
                while (e == IntPtr.Zero)
                {
                    if ((DateTime.UtcNow - zhdal).TotalSeconds >= 15)
                    {
                        zhdal = DateTime.UtcNow;
                        Zhurnal.Pishi("жду игру в RivaTuner (" + string.Join(", ", exes) + "): общая память " + (p == IntPtr.Zero ? "НЕ открыта — RivaTuner не запущен?" : "открыта; RivaTuner видит: " + Vidit(p)));
                    }
                    stop.ThrowIfCancellationRequested();
                    if (p == IntPtr.Zero)
                    {
                        h = OpenFileMapping(4 /* FILE_MAP_READ */, false, "RTSSSharedMemoryV2");
                        if (h != IntPtr.Zero) p = MapViewOfFile(h, 4, 0, 0, UIntPtr.Zero);
                    }
                    if (p != IntPtr.Zero) e = Find(p, exes, out pid);
                    if (e == IntPtr.Zero) { zhdu(); Thread.Sleep(1000); }
                }
                int pos = Marshal.ReadInt32(e + PosOff);
                long front = 0;   // мс, набранные с окном игры впереди
                var buf = new int[1024];
                var t = DateTime.UtcNow;
                while (front < sekund * 1000L)
                {
                    stop.ThrowIfCancellationRequested();
                    Thread.Sleep(250);
                    if (Marshal.ReadInt32(e) != pid) throw new IOException("Игра закрылась раньше, чем закончился замер.");
                    int cur = Marshal.ReadInt32(e + PosOff);
                    var now = DateTime.UtcNow;
                    if (cur != pos)
                    {
                        int n = ((cur - pos) % 1024 + 1024) % 1024;
                        if (ForegroundPid() == pid)
                        {
                            Marshal.Copy(e + BufOff, buf, 0, 1024);
                            for (int i = 0; i < n; i++) { int f = buf[(pos + i) % 1024]; if (f > 0) kadry.Add(f); }
                            front += (long)(now - t).TotalMilliseconds;
                            progress((int)(front / 1000));
                        }
                        pos = cur;
                    }
                    t = now;
                }
                return kadry;
            }
            finally
            {
                if (p != IntPtr.Zero) UnmapViewOfFile(p);
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }

        // Время кадров в мкс → средний FPS, 1% и 0,1% low (как P1/P0.1 в CapFrameX), рывки в минуту
        public static Itog Poschitat(List<int> kadry)
        {
            if (kadry.Count < 100) return null;
            var s = kadry.OrderBy(x => x).ToList();
            double sum = kadry.Sum(x => (double)x), med = s[s.Count / 2];
            Func<double, double> low = share => 1e6 / s[Math.Min(s.Count - 1, (int)(s.Count * (1 - share)))];
            int ryvki = kadry.Count(x => x > 2 * med);   // кадр дольше двух обычных — заметная запинка
            // FPS по полусекундам подряд: сколько игра держит постоянно, без одиночных рывков
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

        // ---------- PresentMon (Intel, MIT): кадры через события Windows, а не через RivaTuner ----------
        // Сверка 04.11 в Deadlock: буфер RivaTuner под своим лимитом показал 1% low 100, PresentMon — 91,8;
        // без лимита RivaTuner оба сходятся (70,2 и 70,4). Поэтому мерим PresentMon, буфер — запасной путь.
        public const string PmUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        public const string PmSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
        public static string PmPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RovnyFPS", "PresentMon-2.6.0-x64.exe"); } }
        public static bool PmEst { get { return File.Exists(PmPath); } }
        public static string PoslednijCsv;
        public static int PoverhSekund;
        public static bool VesCherezWindows;  // последний замер: игра всё время выводила кадры через Windows (окно, оверлей) — так и мерили      // последний замер: сколько секунд кадры шли через Windows поверх игры (не считались)   // первый столбец строк с кадрами — pid окна впереди в момент чтения

        public static void SkachatPm()
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            Directory.CreateDirectory(Path.GetDirectoryName(PmPath));
            string tmp = PmPath + ".new";
            using (var wc = new System.Net.WebClient()) wc.DownloadFile(PmUrl, tmp);
            string sha;
            using (var f = File.OpenRead(tmp)) using (var h = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (sha != PmSha256) { File.Delete(tmp); throw new IOException("PresentMon скачался битым — попробуй ещё раз."); }
            if (File.Exists(PmPath)) File.Delete(PmPath);
            File.Move(tmp, PmPath);
        }

        static int Kolonka(string[] head, params string[] names)
        {
            for (int i = 0; i < head.Length; i++)
                foreach (var n in names) if (string.Equals(head[i].Trim(), n, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Мерит, пока окно игры впереди, sekund секунд. Кадры — в мкс (как буфер RivaTuner), задержки — в мс.
        public static List<int> SobratPm(ICollection<string> exes, int sekund, Action<int> progress, List<double> zaderzhki, CancellationToken stop)
        {
            var args = new StringBuilder();
            foreach (var e in exes) args.Append("--process_name \"" + e + "\" ");
            args.Append("--output_stdout --no_console_stats --v2_metrics --session_name RovnyFps --stop_existing_session");
            var psi = new System.Diagnostics.ProcessStartInfo(PmPath, args.ToString())
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var kadry = new List<int>();
            Zhurnal.Pishi("PresentMon: " + PmPath + " " + args);
            // сырой CSV каждого замера — чтобы цифры итога можно было перепроверить
            string dir = Path.Combine(Path.GetDirectoryName(PmPath), "zamery");
            Directory.CreateDirectory(dir);
            PoslednijCsv = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
            using (var syroe = new StreamWriter(PoslednijCsv, false, new UTF8Encoding(false)))
            using (var p = System.Diagnostics.Process.Start(psi))
            using (stop.Register(() => { try { p.Kill(); } catch (Exception) { } }))   // ReadLine ждёт кадров — отмена его будит
            {
                int oshibok = 0;
                p.ErrorDataReceived += (s2, ev) => { if (ev.Data != null && oshibok++ < 30) Zhurnal.Pishi("PresentMon stderr: " + ev.Data); };   // заодно не забивается буфер
                p.BeginErrorReadLine();
                try
                {
                    string[] head = null; int cPid = -1, cFt = -1, cLat = -1, cMode = -1;
                    // Кадры «через Windows» (PresentMode «Composed…») копим отдельно. Короткие куски на фоне прямого вывода —
                    // оверлей поверх игры, их выбрасываем (04.11 у него 40 FPS вместо 289 → лимит 35). Но у друга 04.11 игра
                    // ВСЁ время шла через Windows (оконный режим) — выброс всех кадров дал «0 из 120» навсегда. Поэтому:
                    // прямых кадров меньше 15 с — это обычный режим игры, считаем всё.
                    var vse = new List<int>(); var cherez = new List<bool>();
                    long frontPryamo = 0, frontVse = 0, porog = 15000, predel = sekund * 4000L;
                    DateTime t = DateTime.UtcNow, hod = DateTime.UtcNow;
                    int strok = 0, neVperedi = 0;
                    string line;
                    Func<bool> hvatit = () => frontPryamo >= sekund * 1000L || (frontVse >= sekund * 1000L && frontPryamo < porog) || frontVse >= predel;
                    while (!hvatit() && (line = p.StandardOutput.ReadLine()) != null)
                    {
                        stop.ThrowIfCancellationRequested();
                        strok++;
                        if ((DateTime.UtcNow - hod).TotalSeconds >= 15)
                        {
                            hod = DateTime.UtcNow;
                            Zhurnal.Pishi("PresentMon: строк " + strok + ", кадров " + vse.Count + ", окно игры не впереди " + neVperedi + ", напрямую " + frontPryamo / 1000 + " с, через Windows " + (frontVse - frontPryamo) / 1000 + " с");
                        }
                        var f = line.Split(',');
                        syroe.WriteLine((head == null ? "" : (ForegroundPid().ToString() + ",")) + line);
                        if (head == null)
                        {
                            if (Kolonka(f, "Application") < 0) continue;
                            head = f;
                            cPid = Kolonka(head, "ProcessID");
                            cFt = Kolonka(head, "MsBetweenPresents", "FrameTime");
                            cLat = Kolonka(head, "MsAllInputToPhotonLatency", "AllInputToPhotonLatency");
                            cMode = Kolonka(head, "PresentMode");
                            if (cPid < 0 || cFt < 0) throw new IOException("PresentMon выдал незнакомые столбцы: " + line);
                            Zhurnal.Pishi("PresentMon: заголовок получен, столбцов " + head.Length);
                            continue;
                        }
                        var now = DateTime.UtcNow;
                        int pid; double ms;
                        if (f.Length <= cFt || !int.TryParse(f[cPid], out pid)) continue;
                        // «Composed: …» — кадры идут через Windows: поверх игры окно или оверлей (счётчик FPS NVIDIA,
                        // уведомление). Игра тогда сама идёт медленнее: 04.11 в Deadlock 40 FPS вместо 289, почти 5 минут
                        // подряд — и подбор поставил лимит 35. Такие кадры в замер не берём.
                        bool cherezWindows = cMode >= 0 && cMode < f.Length && f[cMode].StartsWith("Composed");
                        if (ForegroundPid() == pid)
                        {
                            if (double.TryParse(f[cFt], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0)
                            {
                                vse.Add((int)(ms * 1000)); cherez.Add(cherezWindows);
                                long dt = Math.Min(1000, (long)(now - t).TotalMilliseconds);
                                frontVse += dt; if (!cherezWindows) frontPryamo += dt;
                            }
                            double lat;
                            if (cLat >= 0 && cLat < f.Length && double.TryParse(f[cLat], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat) && lat > 0)
                                zaderzhki.Add(lat);
                            // ход — по тому, что пойдёт в зачёт
                            progress((int)((frontPryamo >= porog ? frontPryamo : frontVse) / 1000));
                        }
                        else neVperedi++;
                        t = now;
                    }
                    VesCherezWindows = frontPryamo < porog;
                    if (VesCherezWindows) { kadry.AddRange(vse); PoverhSekund = 0; }
                    else
                    {
                        for (int i = 0; i < vse.Count; i++) if (!cherez[i]) kadry.Add(vse[i]);
                        PoverhSekund = (int)((frontVse - frontPryamo) / 1000);
                    }
                    Zhurnal.Pishi("PresentMon: конец, строк " + strok + ", кадров в зачёт " + kadry.Count + ", не впереди " + neVperedi +
                        ", напрямую " + frontPryamo / 1000 + " с, через Windows " + (frontVse - frontPryamo) / 1000 + " с" +
                        (VesCherezWindows ? " — игра всё время через Windows, считаю всё" : " — кадры через Windows не считаю") +
                        (p.HasExited ? ", PresentMon вышел сам с кодом " + p.ExitCode : ""));
                    stop.ThrowIfCancellationRequested();
                    if (frontVse < sekund * 1000L && kadry.Count < 100) throw new IOException("Игра закрылась раньше, чем закончился замер.");
                }
                finally
                {
                    try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                    // ETW-сессия могла остаться — снимаем её, иначе следующий замер упрётся в занятое имя
                    try
                    {
                        var k = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PmPath, "--terminate_existing_session --session_name RovnyFps") { UseShellExecute = false, CreateNoWindow = true });
                        k.WaitForExit(5000);
                    }
                    catch (Exception) { }
                }
            }
            return kadry;
        }

        // Лимит: не выше экрана (на 144+ Гц — на 3 ниже, под G-Sync/FreeSync) и с запасом 5% под FPS, который
        // игра держит без лимита постоянно (Itog.Derzhit). Не под 1% low: 04.11 в Deadlock 285 FPS в среднем с
        // редкими рывками по 26 мс дали 1% low 38 и лимит 35 — рывки лимитом не убрать, а игра стала лагать.
        // Кратно 5, не ниже 30.
        public static int VybratLimit(double derzhitBezLimita, int hz)
        {
            int ekran = hz > 60 ? hz - 3 : hz;
            int zapas = (int)(derzhitBezLimita * 0.95) / 5 * 5;
            return Math.Max(30, Math.Min(ekran, zapas));
        }
    }
}
