// Volna4.cs — четвёртая волна: всё остальное из гайда Max.mov, что можно честно проверить на ПК.
// Не вошло (и почему): лимитное подключение для Ethernet — Windows не даёт его поменять без смены прав на системный ключ;
// оверлеи Steam/EA — автор сам говорит, что исправные FPS не снижают; задержка ПК (PC Latency) — PresentMon 2.6 её не даёт,
// а AllInputToPhoton мы уже проверяли (04.11) — заполнен у 15–25% кадров, верить нельзя.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace FpsMaster
{
    static partial class Proverki
    {
        // ---------- 16. G-Sync / FreeSync: что сообщает монитор (EDID) ----------
        class Monitor { public string Imya; public int Min, Max; }

        static List<Monitor> Monitory()
        {
            var r = new List<Monitor>();
            try
            {
                using (var disp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                    foreach (var model in disp.GetSubKeyNames())
                        using (var mk = disp.OpenSubKey(model))
                            foreach (var ekz in mk.GetSubKeyNames())
                                using (var dp = mk.OpenSubKey(ekz + @"\Device Parameters"))
                                {
                                    var e = dp == null ? null : dp.GetValue("EDID") as byte[];
                                    if (e == null || e.Length < 128) continue;
                                    var m = new Monitor { Imya = model };
                                    for (int i = 54; i <= 108; i += 18)
                                    {
                                        if (e[i] != 0 || e[i + 1] != 0) continue;
                                        if (e[i + 3] == 0xFD) { m.Min = e[i + 5]; m.Max = e[i + 6]; }
                                        if (e[i + 3] == 0xFC) m.Imya = Encoding.ASCII.GetString(e, i + 5, 13).Split('\n')[0].Trim();
                                    }
                                    if (m.Imya.Contains("VDD") || m.Imya.ToLowerInvariant().Contains("virtual")) continue;   // виртуальные экраны
                                    if (!r.Any(x => x.Imya == m.Imya)) r.Add(m);
                                }
            }
            catch (Exception ex) { Zhurnal.Pishi("мониторы не прочитать: " + ex.Message); }
            return r;
        }

        static Monitor VrrMonitor()
        {
            return Monitory().Where(m => m.Max >= 60 && m.Max - m.Min >= 30).OrderByDescending(m => m.Max).FirstOrDefault();
        }

        static Proverka Vrr()
        {
            var p = new Proverka { Id = "vrr" };
            var m = VrrMonitor();
            if (m == null) { p.Sost = Vid.Ok; p.Nazvanie = "G-Sync/FreeSync: монитор их не заявляет"; p.Korotko = "не заявлено"; return p; }
            int cur = Pokazat(TekushayaHz());
            p.Sost = Vid.Sovet; p.Nazvanie = "Монитор, возможно, умеет FreeSync";
            p.Pochemu = m.Imya + " сообщает Windows диапазон " + m.Min + "–" + m.Max + " Гц. Такой бывает у мониторов с FreeSync/Adaptive-Sync, но и у обычных — " +
                "наверняка видно только в меню самого монитора. Если там есть FreeSync или Adaptive-Sync: включи, затем панель NVIDIA → «Настройка G-SYNC» → " +
                "включить для полноэкранного и оконного режима. В игре: VSync выключен, Reflex включён, а VSync — в панели NVIDIA для этой игры. " +
                "Так нет ни разрывов, ни задержки обычного VSync (тест автора в CS2: около 14 мс против 44–50)." +
                (m.Max > cur + 1 ? " Ещё: монитор заявляет до " + m.Max + " Гц, а Windows сейчас держит " + cur + " — выше может быть только при другом разрешении." : "");
            p.Seychas = "диапазон " + m.Min + "–" + m.Max + " Гц"; p.Stanet = "проверить меню монитора"; p.Korotko = "диапазон " + m.Min + "–" + m.Max + " Гц";
            p.DeystvieTekst = "Открыть панель NVIDIA";
            p.Deystvie = () => Process.Start("explorer.exe", @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel");
            return p;
        }

        // ---------- 17. Fast Sync для игры (без G-Sync, когда FPS вдвое выше частоты экрана) ----------
        static Proverka FastSyncIgry(Game g, KatkaItog k)
        {
            if (g == null || g.Exes.Count == 0 || !Nvidia.Est) return null;
            string exe = g.Exes[0];
            var p = new Proverka { Id = "fastsync:" + exe };
            if (Nvidia.ZnachenieIgry(exe, Nvidia.VsyncId) == Nvidia.FastSync)
            { p.Sost = Vid.Ok; p.Nazvanie = "Fast Sync в " + g.Name + " включён"; p.Korotko = "включён"; return p; }
            int hz = Pokazat(TekushayaHz());
            string rtss = RtssPapka(); int lim = rtss == null ? 0 : LimitRtss(rtss, exe);
            if (k == null || k.Derzhit < 2 * hz || VrrMonitor() != null || (lim > 0 && lim < 2 * hz)) return null;   // не к месту
            p.Sost = Vid.Problema; p.Nazvanie = "Разрывы картинки в " + g.Name;
            p.Pochemu = "Игра держит " + Itog.Chislo(k.Derzhit) + " FPS при экране " + hz + " Гц — больше вдвое, поэтому видны разрывы. Fast Sync показывает только " +
                "целые кадры и почти не добавляет задержки (тест автора: как без лимита). Цена: шаг кадров менее ровный, чем с лимитом RivaTuner. Ставлю только для этой игры.";
            p.Seychas = "разрывы"; p.Stanet = "Fast Sync"; p.Knopka = "Включить Fast Sync"; p.Korotko = "держит " + Itog.Chislo(k.Derzhit) + " при " + hz + " Гц";
            p.Ispravit = () => Nvidia.PostavitIgre(exe, g.Name, Nvidia.VsyncId, Nvidia.FastSync);
            return p;
        }

        // ---------- 18. Ansel (фоторежим NVIDIA) для игры ----------
        static Proverka AnselIgry(Game g)
        {
            if (g == null || g.Exes.Count == 0 || !Nvidia.Est) return null;
            string exe = g.Exes[0];
            var p = new Proverka { Id = "ansel:" + exe };
            if (Nvidia.ZnachenieIgry(exe, Nvidia.AnselId) == 0u) { p.Sost = Vid.Ok; p.Nazvanie = "Ansel в " + g.Name + " выключен"; p.Korotko = "выключен"; return p; }
            p.Sost = Vid.Problema; p.Nazvanie = "Фоторежим NVIDIA (Ansel) в " + g.Name;
            p.Pochemu = "Драйвер подгружает в игру фоторежим Ansel. Были случаи, когда он снижал FPS (Reddit; замеров у автора нет). " +
                "Если не снимаешь им скриншоты — выключить без потерь. Только для этой игры.";
            p.Seychas = "разрешён"; p.Stanet = "выключен"; p.Knopka = "Выключить"; p.Korotko = "разрешён";
            p.Ispravit = () => Nvidia.PostavitIgre(exe, g.Name, Nvidia.AnselId, 0);
            return p;
        }

        // ---------- 19. HDCP — с ценой ----------
        static string hdcpKlyuch;
        static string HdcpKlyuch()
        {
            if (hdcpKlyuch != null) return hdcpKlyuch;
            hdcpKlyuch = "";
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(cls))
                    foreach (var n in k.GetSubKeyNames())
                    {
                        if (n.Length != 4) continue;
                        using (var s = k.OpenSubKey(n))
                            if (s != null && (s.GetValue("DriverDesc") ?? "").ToString().Contains("NVIDIA")) { hdcpKlyuch = cls + "\\" + n; break; }
                    }
            }
            catch (Exception) { }
            return hdcpKlyuch;
        }

        static Proverka Hdcp()
        {
            string k = HdcpKlyuch(); if (k == "") return null;
            var p = new Proverka { Id = "hdcp" };
            object v = Chitat(Registry.LocalMachine, k, "RMHdcpKeyglobZero");
            if (v is int && (int)v == 1) { p.Sost = Vid.Ok; p.Nazvanie = "HDCP выключен"; p.Korotko = "выключен"; return p; }
            p.Sost = Vid.Risk; p.Nazvanie = "HDCP (защита видео) включён";
            p.Pochemu = "Защита видео от копирования между видеокартой и монитором. По форумам, может отнимать немного производительности и добавлять задержку — " +
                "замеров нет ни у автора, ни у нас. Действует после перезагрузки.";
            p.Seychas = "включён"; p.Stanet = "выключен"; p.Knopka = "Выключить"; p.Korotko = "включён · риск";
            p.Cena = "Перестанут показываться фильмы с защитой: Netflix, онлайн-кинотеатры в HD, Blu-ray. Вернуть — кнопкой «вернуть» и перезагрузкой.";
            p.Ispravit = () => { string old = v is int ? v.ToString() : "-"; Pisat(Registry.LocalMachine, k, "RMHdcpKeyglobZero", "1"); Zhurnal.Pishi("HDCP выключен (было " + old + ")"); return old; };
            return p;
        }

        // ---------- 20. Store сам обновляет приложения ----------
        const string StoreKlyuch = @"SOFTWARE\Policies\Microsoft\WindowsStore";

        static Proverka Store()
        {
            var p = new Proverka { Id = "store" };
            object v = Chitat(Registry.LocalMachine, StoreKlyuch, "AutoDownload");
            if (v is int && (int)v == 2) { p.Sost = Vid.Ok; p.Nazvanie = "Store не качает обновления сам"; p.Korotko = "вручную"; return p; }
            p.Sost = Vid.Problema; p.Nazvanie = "Store сам качает обновления";
            p.Pochemu = "Microsoft Store в фоне скачивает и ставит обновления приложений — это может совпасть с каткой и занять сеть и диск. " +
                "Обновлять можно вручную: Store → Загрузки → «Получить обновления».";
            p.Seychas = "автоматически"; p.Stanet = "вручную"; p.Knopka = "Выключить"; p.Korotko = "автоматически";
            p.Ispravit = () => { string old = v is int ? v.ToString() : "-"; Pisat(Registry.LocalMachine, StoreKlyuch, "AutoDownload", "2"); Zhurnal.Pishi("Store: AutoDownload → 2 (было " + old + ")"); return old; };
            return p;
        }

        // ---------- 21. Edge работает в фоне ----------
        const string EdgeKlyuch = @"SOFTWARE\Policies\Microsoft\Edge";

        static Proverka Edge()
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            if (!File.Exists(exe)) return null;
            var p = new Proverka { Id = "edge" };
            object a = Chitat(Registry.LocalMachine, EdgeKlyuch, "StartupBoostEnabled"), b = Chitat(Registry.LocalMachine, EdgeKlyuch, "BackgroundModeEnabled");
            if (a is int && (int)a == 0 && b is int && (int)b == 0) { p.Sost = Vid.Ok; p.Nazvanie = "Edge не висит в фоне"; p.Korotko = "не висит"; return p; }
            p.Sost = Vid.Problema; p.Nazvanie = "Edge работает в фоне";
            p.Pochemu = "Edge стартует вместе с Windows («ускорение запуска») и не закрывается до конца, чтобы потом открываться быстрее, — держит память и процессор, пока ты играешь. " +
                "Выключаю обе вещи политиками Edge; в его настройках появится надпись «Управляется организацией» — это из-за них.";
            p.Seychas = "в фоне"; p.Stanet = "закрывается целиком"; p.Knopka = "Выключить"; p.Korotko = "в фоне";
            p.Ispravit = () =>
            {
                string old = (a is int ? a.ToString() : "-") + ";" + (b is int ? b.ToString() : "-");
                Pisat(Registry.LocalMachine, EdgeKlyuch, "StartupBoostEnabled", "0");
                Pisat(Registry.LocalMachine, EdgeKlyuch, "BackgroundModeEnabled", "0");
                Zhurnal.Pishi("Edge: фон выключен (было " + old + ")");
                return old;
            };
            return p;
        }

        // ---------- 22. фильтрация текстур в CS2 (только совет, по файлу настроек игры) ----------
        static Proverka FiltraciyaCs2(Game g)
        {
            if (g == null || g.Name != "CS2") return null;
            string steam = Chitat(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") as string;
            if (string.IsNullOrEmpty(steam)) return null;
            string ud = Path.Combine(steam.Replace('/', '\\'), "userdata");
            if (!Directory.Exists(ud)) return null;
            var f = Directory.GetDirectories(ud).Select(d => Path.Combine(d, @"730\local\cfg\cs2_video.txt")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (f == null) return null;
            int v = -1;
            foreach (var line in File.ReadAllLines(f))
                if (line.Contains("\"setting.r_texturefilteringquality\""))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, "\"(\\d+)\"\\s*$");
                    if (m.Success) v = int.Parse(m.Groups[1].Value);
                }
            if (v < 0) return null;
            string[] imena = { "билинейная", "трилинейная", "анизотропная 2x", "анизотропная 4x", "анизотропная 8x", "анизотропная 16x" };
            string sey = v < imena.Length ? imena[v] : v.ToString();
            var p = new Proverka { Id = "cs2filtr" };
            if (v == 0) { p.Sost = Vid.Ok; p.Nazvanie = "Фильтрация текстур в CS2 — билинейная"; p.Korotko = sey; return p; }
            p.Sost = Vid.Sovet; p.Nazvanie = "Фильтрация текстур в CS2";
            p.Pochemu = "В тесте автора полное выключение анизотропной фильтрации дало CS2 +2–3 FPS — немного, сверяй по замеру. " +
                "В CS2: Настройки → Видео → Расширенные → «Режим фильтрации текстур» → «Билинейная». Вдали текстуры станут чуть мыльнее.";
            p.Seychas = sey; p.Stanet = "билинейная"; p.Korotko = sey;
            return p;
        }

        static Action<string> Vozvrat4(string id)
        {
            switch (id)
            {
                case "hdcp": return old => { Pisat(Registry.LocalMachine, HdcpKlyuch(), "RMHdcpKeyglobZero", old); Zhurnal.Pishi("HDCP → " + old); };
                case "store": return old => { Pisat(Registry.LocalMachine, StoreKlyuch, "AutoDownload", old); Zhurnal.Pishi("Store: AutoDownload → " + old); };
                case "edge": return old =>
                {
                    var c = old.Split(';');
                    Pisat(Registry.LocalMachine, EdgeKlyuch, "StartupBoostEnabled", c[0]);
                    Pisat(Registry.LocalMachine, EdgeKlyuch, "BackgroundModeEnabled", c.Length > 1 ? c[1] : "-");
                    Zhurnal.Pishi("Edge: фон возвращён (" + old + ")");
                };
            }
            return null;
        }
    }
}
