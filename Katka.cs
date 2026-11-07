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
        // когда игра последний раз была НЕ впереди (тики UTC): кадр, начавшийся раньше, — кадр альт-таба, даже если кончился
        // уже в игре (04.11 22:54: свёрнутая игра выдала кадр в 114 с при возврате, и он попал в «в игре»)
        long neVperediDo;

        // кадры целиком (40 минут при 300 FPS — около 720 тыс., это ~4 МБ)
        readonly List<int> ft = new List<int>();          // мкс
        readonly List<byte> st = new List<byte>();        // состояние
        readonly List<bool> cherez = new List<bool>();    // через Windows
        readonly List<double> kogda = new List<double>(); // секунды от начала
        readonly List<string> kto = new List<string>();   // что было впереди в момент кадра (по секундам врало: 04.11 «альт-таб, впереди Deadlock»)
        // кто держал кадр: сколько над ним работали процессор и видеокарта (PresentMon CPUBusy / GPUBusy), мкс, -1 — нет данных
        readonly List<int> cpuB = new List<int>(), gpuB = new List<int>();
        // раз в секунду — что творилось в системе: память, подкачка, диск, процессор
        readonly List<Obrazec> obrazcy = new List<Obrazec>();
        readonly object zamok = new object();
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
            Pribory pr = null; int tik = 0; DateTime igraZhilaDo = DateTime.UtcNow;
            while (!stop)
            {
                if (tik++ % 4 == 0)
                {
                    // 07.11: игра закрылась, а PresentMon с --terminate_on_proc_exit висел 10+ мин — итог не писался.
                    // Игры нет 15 с — закрываем PresentMon сами, чтение кончится и итог посчитается.
                    bool zhiva = g.Exes.Any(e => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e)).Length > 0);
                    if (zhiva || pidIgry == 0) igraZhilaDo = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - igraZhilaDo).TotalSeconds > 15)
                    {
                        var p = pm;
                        try { if (p != null && !p.HasExited) { Zhurnal.Pishi("замер в игре: игры нет 15 с, а PresentMon не вышел — закрываю его"); p.Kill(); } } catch (Exception) { }
                        igraZhilaDo = DateTime.UtcNow;
                    }
                }
                if (tik % 4 == 1)
                {
                    try
                    {
                        if (pr == null) pr = new Pribory();
                        var o = pr.Snyat(pidIgry);
                        o.Sek = (int)(DateTime.Now - nachalo).TotalSeconds;
                        lock (zamok) obrazcy.Add(o);
                    }
                    catch (Exception ex) { if (tik < 8) Zhurnal.Pishi("замер в игре: приборы не снимаются: " + ex.Message); }
                }
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
                        Interlocked.Exchange(ref neVperediDo, DateTime.UtcNow.Ticks);
                    }
                }
                catch (Exception) { }
                Thread.Sleep(250);
            }
        }

        static int Mks(string[] f, int c)
        {
            double ms;
            if (c < 0 || c >= f.Length || !double.TryParse(f[c], NumberStyles.Float, CultureInfo.InvariantCulture, out ms) || ms < 0) return -1;
            return (int)(ms * 1000);
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
                // PresentMon может выйти сам посреди катки (04.11 21:49: игра 4 мин свёрнута, потом он выдал хвост и вышел,
                // игра осталась запущенной — катка разломилась на две). Игра жива — запускаем его снова, катка та же.
                int zapuskov = 0, oshibok = 0;
                var ci = CultureInfo.InvariantCulture;
                while (!stop)
                {
                pm = Process.Start(new ProcessStartInfo(Zamer.PmPath, args.ToString())
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                pm.ErrorDataReceived += (s2, ev) => { if (ev.Data != null && oshibok++ < 30) Zhurnal.Pishi("замер в игре, PresentMon stderr: " + ev.Data); };
                pm.BeginErrorReadLine();
                if (zapuskov == 0) Zhurnal.Pishi("замер в игре: PresentMon " + args);
                string[] head = null; int cPid = -1, cFt = -1, cMode = -1, cCpu = -1, cGpu = -1, strok = 0;
                string line;
                while (!stop && (line = pm.StandardOutput.ReadLine()) != null)
                {
                    strok++;
                    var f = line.Split(',');
                    if (head == null)
                    {
                        if (f.Length == 0 || f[0] != "Application") continue;
                        head = f;
                        cPid = Array.IndexOf(head, "ProcessID"); cMode = Array.IndexOf(head, "PresentMode");
                        cFt = Array.IndexOf(head, "MsBetweenPresents"); if (cFt < 0) cFt = Array.IndexOf(head, "FrameTime");
                        if (cPid < 0 || cFt < 0) throw new IOException("PresentMon выдал незнакомые столбцы: " + line);
                        cCpu = Array.IndexOf(head, "CPUBusy"); cGpu = Array.IndexOf(head, "GPUBusy");
                        if (cCpu < 0 || cGpu < 0) Zhurnal.Pishi("замер в игре: нет столбцов CPUBusy/GPUBusy — причину рывков по кадру не узнать");
                        continue;
                    }
                    int pid; double ms;
                    if (f.Length <= Math.Max(cFt, cPid) || !int.TryParse(f[cPid], out pid) || !double.TryParse(f[cFt], NumberStyles.Float, ci, out ms) || ms <= 0) continue;
                    pidIgry = pid;
                    double sek = (DateTime.Now - nachalo).TotalSeconds;
                    string v = vperedi; byte so = sost;
                    if (so <= Menu && DateTime.UtcNow.Ticks - (long)(ms * TimeSpan.TicksPerMillisecond) < Interlocked.Read(ref neVperediDo) + 3000000)
                    { so = AltTab; v = ""; }   // +0,3 с — на шаг наблюдателя
                    ft.Add((int)(ms * 1000)); st.Add(so); kogda.Add(sek); kto.Add(v);
                    cpuB.Add(Mks(f, cCpu)); gpuB.Add(Mks(f, cGpu));
                    cherez.Add(cMode >= 0 && cMode < f.Length && f[cMode].StartsWith("Composed"));
                    int s = (int)sek; if (!okno.ContainsKey(s)) okno[s] = v;
                }
                if (stop) break;
                string kod = "?";
                try { if (pm.WaitForExit(3000)) kod = pm.ExitCode.ToString(); } catch (Exception) { }
                bool igraZhiva = g.Exes.Any(e => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e)).Length > 0);
                Zhurnal.Pishi("замер в игре: PresentMon вышел сам (код " + kod + ", строк " + strok + ", кадров всего " + ft.Count + "), игра " +
                    (igraZhiva ? (zapuskov < 10 ? "ещё запущена — запускаю его снова" : "запущена, но перезапусков уже 10 — заканчиваю") : "закрыта"));
                if (!igraZhiva || ++zapuskov > 10) break;
                Thread.Sleep(1000);
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
            // рывки в игре и в меню: кадр втрое дольше обычного (и не короче 40 мс) — от чего каждый
            // 04.11 у него: 13 одиночных подвисаний по 60–85 мс за 3 мин боя уронили 1% low с ~55 до 29,6
            var obych = Enumerable.Range(0, ft.Count).Where(i => st[i] <= Menu).Select(i => ft[i]).OrderBy(x => x).ToList();
            int porog = obych.Count > 0 ? Math.Max(40000, obych[obych.Count / 2] * 3) : 40000;
            var ryvki = Enumerable.Range(0, ft.Count).Where(i => st[i] <= Menu && ft[i] >= porog).ToList();
            string glavnaya = null;
            sb.AppendLine("ПОДВИСАНИЯ В ИГРЕ И МЕНЮ (кадр дольше " + Ch(porog / 1000.0) + " мс): " + ryvki.Count);
            if (ryvki.Count > 0)
            {
                var poPrichine = ryvki.Select(i => { string kor; Prichina(i, out kor); return kor; }).GroupBy(x => x).OrderByDescending(x => x.Count()).ToList();
                foreach (var gr in poPrichine)
                {
                    int i0 = ryvki.First(i => { string kor; Prichina(i, out kor); return kor == gr.Key; });
                    string kor0; sb.AppendLine("  " + gr.Count() + " — " + Prichina(i0, out kor0));
                }
                glavnaya = poPrichine[0].Key;
            }
            sb.AppendLine("  (по кадру — сколько над ним работали процессор и видеокарта; по системе — память, подкачка, диск, ядра рядом по времени)");
            sb.AppendLine();
            // самые долгие кадры с причиной
            // в игре и меню — отдельно: 04.11 21:39 список из 15 забили кадры свёрнутой игры по 3 минуты, а подвисания в бою ушли вниз
            sb.AppendLine("САМЫЕ ДОЛГИЕ КАДРЫ В ИГРЕ И МЕНЮ:");
            var vIgreKadry = Enumerable.Range(0, ft.Count).Where(i => st[i] <= Menu).OrderByDescending(i => ft[i]).Take(15).ToList();
            var vneKadry = Enumerable.Range(0, ft.Count).Where(i => st[i] > Menu).OrderByDescending(i => ft[i]).Take(5).ToList();
            if (vIgreKadry.Count == 0) sb.AppendLine("  нет");
            for (int n = 0; n < vIgreKadry.Count + vneKadry.Count; n++)
            {
                if (n == vIgreKadry.Count) { sb.AppendLine(); sb.AppendLine("САМЫЕ ДОЛГИЕ КАДРЫ, ПОКА ИГРА БЫЛА НЕ ВПЕРЕДИ (не в зачёт):"); }
                int i = n < vIgreKadry.Count ? vIgreKadry[n] : vneKadry[n - vIgreKadry.Count];
                string kor, pr = st[i] <= Menu ? Prichina(i, out kor) : null;
                sb.AppendLine("  " + nachalo.AddSeconds(kogda[i]).ToString("HH:mm:ss") + "  " + Ch(ft[i] / 1000.0) + " мс  — " + Imena[st[i]] +
                    (cherez[i] ? ", через Windows" : "") + (st[i] >= AltTab && !string.IsNullOrEmpty(kto[i]) ? ", впереди " + kto[i] : "") +
                    (pr != null ? ": " + pr : ""));
                sb.AppendLine("      " + Cifry(i));
            }
            // по секундам — в CSV рядом
            string dir = Path.Combine(Path.GetDirectoryName(Zamer.PmPath), "zamery", "katki");
            Directory.CreateDirectory(dir);
            string imya = nachalo.ToString("yyyy-MM-dd_HH-mm") + "_" + string.Concat(g.Name.Split(Path.GetInvalidFileNameChars()));
            var csv = new StringBuilder("секунда;время;кадров;FPS;самый долгий мс;состояние;через Windows %;впереди;" +
                "видеокарта занята %;процессор %;самое загруженное ядро %;игра % процессора;свободно памяти МБ;подкачка страниц/с;чтение с диска МБ/с\r\n");
            int j = 0;
            var poSek = new Dictionary<int, Obrazec>();
            lock (zamok) foreach (var o in obrazcy) poSek[o.Sek] = o;
            for (int s = 0; s <= (int)vsego; s++)
            {
                int n = 0, cw = 0, mx = 0; long sum = 0, gp = 0; bool gpEst = false; var cnt = new int[Imena.Length];
                while (j < ft.Count && (int)kogda[j] == s)
                {
                    n++; sum += ft[j]; mx = Math.Max(mx, ft[j]); cnt[st[j]]++; if (cherez[j]) cw++;
                    if (gpuB[j] >= 0) { gp += gpuB[j]; gpEst = true; }
                    j++;
                }
                if (n == 0) continue;
                int glavnoe = Array.IndexOf(cnt, cnt.Max());
                string ktoS; okno.TryGetValue(s, out ktoS);
                Obrazec ob; poSek.TryGetValue(s, out ob);
                csv.Append(s + ";" + nachalo.AddSeconds(s).ToString("HH:mm:ss") + ";" + n + ";" + Ch(n * 1e6 / sum) + ";" + Ch(mx / 1000.0) + ";" + Imena[glavnoe] + ";" + (100 * cw / n) + ";" + ktoS + ";" +
                    (gpEst ? Math.Min(100, 100 * gp / sum).ToString() : "") + ";" +
                    (ob != null ? Ch0(ob.Cpu) + ";" + Ch0(ob.MaxYadro) + ";" + Ch0(ob.Igra) + ";" + (ob.SvobodnoMb >= 0 ? ob.SvobodnoMb.ToString() : "") + ";" + Ch0(ob.StranicVSek) + ";" + Ch0(ob.ChtenieMb) : ";;;;;") + "\r\n");
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
                "\n" + (ryvki.Count > 0 ? "Подвисаний " + ryvki.Count + ", чаще всего — " + glavnaya + ". " : "") +
                (prochee.Count > 0 ? string.Join(", ", prochee) + " — не в зачёт. " : "") + "Подробно — «Журнал».";
        }

        // ---------- от чего кадр был долгим ----------
        // что было в системе рядом с кадром: PresentMon отдаёт кадры с запаздыванием до ~2 с, поэтому окно −2…+1 с
        Obrazec Ryadom(int i)
        {
            int s = (int)kogda[i];
            Obrazec r = null;
            lock (zamok)
                foreach (var o in obrazcy)
                {
                    if (o.Sek < s - 2 || o.Sek > s + 1) continue;
                    if (r == null) { r = new Obrazec { Sek = s, VsegoMb = o.VsegoMb, SvobodnoMb = o.SvobodnoMb }; }
                    if (o.SvobodnoMb >= 0 && (r.SvobodnoMb < 0 || o.SvobodnoMb < r.SvobodnoMb)) r.SvobodnoMb = o.SvobodnoMb;
                    r.Cpu = Math.Max(r.Cpu, o.Cpu); r.MaxYadro = Math.Max(r.MaxYadro, o.MaxYadro); r.Igra = Math.Max(r.Igra, o.Igra);
                    r.StranicVSek = Math.Max(r.StranicVSek, o.StranicVSek); r.ChtenieMb = Math.Max(r.ChtenieMb, o.ChtenieMb);
                }
            return r;
        }

        string Prichina(int i, out string korotko)
        {
            return Prichina(ft[i] / 1000.0, cpuB[i] / 1000.0, gpuB[i] / 1000.0, Ryadom(i), out korotko);
        }

        string Cifry(int i)
        {
            var o = Ryadom(i);
            return "кадр: процессор " + (cpuB[i] >= 0 ? Ch(cpuB[i] / 1000.0) + " мс" : "—") + ", видеокарта " + (gpuB[i] >= 0 ? Ch(gpuB[i] / 1000.0) + " мс" : "—") +
                (o == null ? "; система: нет данных" :
                 "; система: свободно " + (o.SvobodnoMb >= 0 ? o.SvobodnoMb + " МБ" : "—") + ", подкачка " + Ch0(o.StranicVSek) + " стр/с, диск " + Ch0(o.ChtenieMb) +
                 " МБ/с, процессор " + Ch0(o.Cpu) + "%, самое загруженное ядро " + Ch0(o.MaxYadro) + "%, игра " + Ch0(o.Igra) + "%");
        }

        // f, c, gp — мс (c, gp < 0 — нет данных), o — система рядом по времени (null — нет). Пороги:
        //   видеокарта работала над кадром ≥ 60% его времени — не успела она;
        //   подкачка ≥ 1000 стр/с (4 МБ/с с диска в память) при свободной памяти меньше 800 МБ или 5% — не хватило памяти;
        //   та же подкачка или чтение ≥ 30 МБ/с при свободной памяти — игра грузила с диска;
        //   ядро ≥ 95% — упёрлись в процессор; иначе — заминка в игре или драйвере (часто это сборка шейдеров).
        public static string Prichina(double f, double c, double gp, Obrazec o, out string korotko)
        {
            if (gp >= 0 && gp >= 0.6 * f) { korotko = "видеокарта"; return "видеокарта не успевала"; }
            if (o != null)
            {
                bool malo = o.SvobodnoMb >= 0 && o.SvobodnoMb < Math.Max(800, o.VsegoMb / 20);
                if (o.StranicVSek >= 1000 && malo) { korotko = "не хватило памяти"; return "не хватило памяти — Windows дочитывала игру с диска (подкачка)"; }
                if (o.StranicVSek >= 1000 || o.ChtenieMb >= 30) { korotko = "чтение с диска"; return "игра подгружала с диска (карта, модели, шейдеры)"; }
                if (malo) { korotko = "мало памяти"; return "свободной памяти почти не осталось"; }
                if (o.MaxYadro >= 95) { korotko = "процессор"; return "процессор: самое загруженное ядро на пределе"; }
                if (c < 0 && gp < 0) { korotko = "?"; return "причину не узнать: PresentMon не дал данных по кадру"; }
                korotko = "заминка в игре или драйвере";
                return "заминка в самой игре или драйвере при свободных памяти, диске и процессоре — похоже на сборку шейдеров";
            }
            if (c < 0 && gp < 0) { korotko = "?"; return "причину не узнать: нет данных ни по кадру, ни по системе"; }
            korotko = "процессор или игра"; return "кадр держал процессор (игра или драйвер), данных по системе нет";
        }

        static string Ch0(float v) { return v < 0 ? "" : Math.Round(v).ToString(); }

        static string Min(double sekund) { return sekund >= 90 ? Math.Round(sekund / 60) + " мин" : Math.Round(sekund) + " с"; }
    }

    // что было в системе за последнюю секунду; −1 — не снялось
    class Obrazec
    {
        public int Sek, SvobodnoMb = -1, VsegoMb = -1;
        public float Cpu = -1, MaxYadro = -1, Igra = -1, StranicVSek = -1, ChtenieMb = -1;
    }

    // Приборы раз в секунду. Память и общий процессор — через WinAPI (работает везде), подкачка, диск и ядра — счётчиками
    // производительности; счётчик, который не завёлся (другая Windows, отключены), пишется в журнал и просто пропускается.
    class Pribory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMSTAT { public uint dwLength, dwMemoryLoad; public ulong total, avail, totalPage, availPage, totalVirt, availVirt, availExt; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        readonly PerformanceCounter stranicy, disk;
        readonly PerformanceCounter[] yadra;
        long pIdle, pKern, pUser; TimeSpan pIgra; int pPid; DateTime pKogda;

        public Pribory()
        {
            stranicy = Schetchik("Memory", "Pages Input/sec", "");
            disk = Schetchik("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            try
            {
                yadra = new PerformanceCounterCategory("Processor").GetInstanceNames().Where(x => x != "_Total")
                    .Select(x => Schetchik("Processor", "% Processor Time", x)).Where(x => x != null).ToArray();
            }
            catch (Exception ex) { Zhurnal.Pishi("замер в игре: счётчиков ядер нет: " + ex.Message); yadra = new PerformanceCounter[0]; }
            GetSystemTimes(out pIdle, out pKern, out pUser); pKogda = DateTime.UtcNow;
            Zhurnal.Pishi("замер в игре: приборы — подкачка " + (stranicy != null ? "есть" : "нет") + ", диск " + (disk != null ? "есть" : "нет") + ", ядер " + yadra.Length);
        }

        static PerformanceCounter Schetchik(string kat, string imya, string ekz)
        {
            try { var c = new PerformanceCounter(kat, imya, ekz, true); c.NextValue(); return c; }
            catch (Exception ex) { Zhurnal.Pishi("замер в игре: счётчик " + kat + "\\" + imya + " недоступен: " + ex.Message); return null; }
        }

        public Obrazec Snyat(int pid)
        {
            var o = new Obrazec();
            var m = new MEMSTAT { dwLength = (uint)Marshal.SizeOf(typeof(MEMSTAT)) };
            if (GlobalMemoryStatusEx(ref m)) { o.SvobodnoMb = (int)(m.avail >> 20); o.VsegoMb = (int)(m.total >> 20); }
            long i, k, u;
            if (GetSystemTimes(out i, out k, out u))
            {
                long vse = (k - pKern) + (u - pUser), prostoy = i - pIdle;   // время ядра включает простой
                if (vse > 0) o.Cpu = (float)(100.0 * (vse - prostoy) / vse);
                pIdle = i; pKern = k; pUser = u;
            }
            var t = DateTime.UtcNow; double sek = (t - pKogda).TotalSeconds; pKogda = t;
            if (pid != 0)
                try
                {
                    TimeSpan igra;
                    using (var p = Process.GetProcessById(pid)) igra = p.TotalProcessorTime;
                    if (pid == pPid && sek > 0) o.Igra = (float)(100.0 * (igra - pIgra).TotalSeconds / sek / Environment.ProcessorCount);
                    pIgra = igra; pPid = pid;
                }
                catch (Exception) { }
            if (stranicy != null) try { o.StranicVSek = stranicy.NextValue(); } catch (Exception) { }
            if (disk != null) try { o.ChtenieMb = disk.NextValue() / 1048576f; } catch (Exception) { }
            foreach (var c in yadra) try { o.MaxYadro = Math.Max(o.MaxYadro, c.NextValue()); } catch (Exception) { }
            return o;
        }
    }

    // доступ к Itog.Chislo без путаницы с методом Katka.Itog()
    static class Itog_ { public static string Chislo(double v) { return RovnyFps.Itog.Chislo(v); } }
}
