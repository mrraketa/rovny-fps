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
        public double? ZaderzhkaMs;   // от мыши до экрана, медиана; только из PresentMon
        public bool Tochno;           // true — PresentMon; false — буфер RivaTuner (под своим лимитом ровнее, чем есть)
        public static string Chislo(double v) { return v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")); }
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

        // Ждёт, пока RivaTuner увидит игру, и собирает время кадров, пока окно игры впереди
        // (в фоне игры сами себя тормозят). progress(сколько секунд набрано).
        public static List<int> Sobrat(ICollection<string> exes, int sekund, Action<int> progress, Func<bool> zhdu, CancellationToken stop)
        {
            IntPtr h = IntPtr.Zero, p = IntPtr.Zero;
            var kadry = new List<int>();
            try
            {
                IntPtr e = IntPtr.Zero; int pid = 0;
                while (e == IntPtr.Zero)
                {
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
            return new Itog { Kadrov = kadry.Count, Sredniy = 1e6 * kadry.Count / sum, Low1 = low(0.01), Low01 = low(0.001), RyvkovVMin = ryvki / (sum / 6e7) };
        }

        // ---------- PresentMon (Intel, MIT): кадры через события Windows, а не через RivaTuner ----------
        // Сверка 04.11 в Deadlock: буфер RivaTuner под своим лимитом показал 1% low 100, PresentMon — 91,8;
        // без лимита RivaTuner оба сходятся (70,2 и 70,4). Поэтому мерим PresentMon, буфер — запасной путь.
        public const string PmUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        public const string PmSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
        public static string PmPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RovnyFPS", "PresentMon-2.6.0-x64.exe"); } }
        public static bool PmEst { get { return File.Exists(PmPath); } }
        public static string PoslednijCsv;   // первый столбец строк с кадрами — pid окна впереди в момент чтения

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
            // сырой CSV каждого замера — чтобы цифры итога можно было перепроверить
            string dir = Path.Combine(Path.GetDirectoryName(PmPath), "zamery");
            Directory.CreateDirectory(dir);
            PoslednijCsv = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
            using (var syroe = new StreamWriter(PoslednijCsv, false, new UTF8Encoding(false)))
            using (var p = System.Diagnostics.Process.Start(psi))
            using (stop.Register(() => { try { p.Kill(); } catch (Exception) { } }))   // ReadLine ждёт кадров — отмена его будит
            {
                p.ErrorDataReceived += delegate { };   // stderr читаем, чтобы не забился буфер
                p.BeginErrorReadLine();
                try
                {
                    string[] head = null; int cPid = -1, cFt = -1, cLat = -1;
                    long front = 0; DateTime t = DateTime.UtcNow;
                    string line;
                    while (front < sekund * 1000L && (line = p.StandardOutput.ReadLine()) != null)
                    {
                        stop.ThrowIfCancellationRequested();
                        var f = line.Split(',');
                        syroe.WriteLine((head == null ? "" : (ForegroundPid().ToString() + ",")) + line);
                        if (head == null)
                        {
                            if (Kolonka(f, "Application") < 0) continue;
                            head = f;
                            cPid = Kolonka(head, "ProcessID");
                            cFt = Kolonka(head, "MsBetweenPresents", "FrameTime");
                            cLat = Kolonka(head, "MsAllInputToPhotonLatency", "AllInputToPhotonLatency");
                            if (cPid < 0 || cFt < 0) throw new IOException("PresentMon выдал незнакомые столбцы: " + line);
                            continue;
                        }
                        var now = DateTime.UtcNow;
                        int pid; double ms;
                        if (f.Length <= cFt || !int.TryParse(f[cPid], out pid)) continue;
                        if (ForegroundPid() == pid)
                        {
                            if (double.TryParse(f[cFt], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0)
                                kadry.Add((int)(ms * 1000));
                            double lat;
                            if (cLat >= 0 && cLat < f.Length && double.TryParse(f[cLat], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat) && lat > 0)
                                zaderzhki.Add(lat);
                            front += Math.Min(1000, (long)(now - t).TotalMilliseconds);
                            progress((int)(front / 1000));
                        }
                        t = now;
                    }
                    stop.ThrowIfCancellationRequested();
                    if (front < sekund * 1000L && kadry.Count < 100) throw new IOException("Игра закрылась раньше, чем закончился замер.");
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

        // Лимит: не выше экрана (на 144+ Гц — на 3 ниже, под G-Sync/FreeSync) и с запасом 5% под 1% low
        // без лимита — тогда компьютер держит его и в самых тяжёлых местах. Кратно 5, не ниже 30.
        public static int VybratLimit(double low1BezLimita, int hz)
        {
            int ekran = hz > 60 ? hz - 3 : hz;
            int zapas = (int)(low1BezLimita * 0.95) / 5 * 5;
            return Math.Max(30, Math.Min(ekran, zapas));
        }
    }
}
