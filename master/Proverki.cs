// Proverki.cs — проверки первой волны. Каждая: что сейчас, почему это важно, что станет; исправление — только по кнопке,
// с запоминанием прежнего значения и кнопкой «вернуть». Советы (то, что программа сама не меняет) — с пояснением, что сделать.
// Источник советов — разбор гайда Max.mov (rovny-fps\research\razbor-max-mov-windows11.md).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FpsMaster
{
    enum Vid { Ok, Problema, Sovet, Risk, NetDannyh }

    class Proverka
    {
        public string Id, Nazvanie, Pochemu = "", Seychas = "", Stanet = "", Knopka, Korotko = "";
        public Vid Sost;
        public bool Sdelano;                 // исправлено нами, можно вернуть
        public Func<string> Ispravit;        // возвращает прежнее значение (для «вернуть»); null — это совет
        public Action<string> Vernut;
        public string DeystvieTekst;         // для советов: кнопка-помощник («Открыть автозагрузку»)
        public Action Deystvie;
        public bool Mozhno { get { return Ispravit != null && (Sost == Vid.Problema || Sost == Vid.Risk); } }
        public string Cena;                  // для «риска»: чем платишь — спрашивается перед исправлением
        public bool Nado { get { return Sost == Vid.Problema || Sost == Vid.Sovet || Sost == Vid.Risk; } }
    }

    static partial class Proverki
    {
        // fixy: id → прежнее значение (что вернуть); k — последняя катка выбранной игры (может быть null)
        public static List<Proverka> Sobrat(Dictionary<string, string> fixy, KatkaItog k, Game g)
        {
            var r = new List<Proverka>();
            Action<Func<Proverka>> d = f =>
            {
                try { var p = f(); if (p != null) { p.Sdelano = fixy.ContainsKey(p.Id); r.Add(p); } }
                catch (Exception ex) { Zhurnal.Pishi("проверка упала: " + ex.Message); }
            };
            d(Chastota); d(Dvr); d(Obsluzhivanie);
            d(() => Vyvod(k)); d(OverleyNvidia); d(Periferiya); d(() => DiskIgry(k)); d(() => Pamyat(k));
            // волна 2 — видеокарта и игра
            d(() => Pitanie(g)); d(() => Upor(k, g)); d(() => Limit(g, k));
            // волна 3 — то, где есть цена, и железо
            d(Vbs); d(Drayver); d(Rebar); d(Msi);
            // волна 4 — остальное из гайда
            d(Vrr); d(() => FastSyncIgry(g, k)); d(() => AnselIgry(g)); d(Hdcp); d(Store); d(Edge); d(() => FiltraciyaCs2(g));
            // волна 5 — следы твикеров
            try { foreach (var p in Tvikery(fixy, g)) { p.Sdelano = fixy.ContainsKey(p.Id) && p.Sost == Vid.Ok; r.Add(p); } }   // след вернулся — снова проблема
            catch (Exception ex) { Zhurnal.Pishi("следы твикеров: " + ex.Message); }
            return r;
        }

        // как вернуть то, что исправили (проверка после исправления уже «в порядке», своей кнопки «вернуть» у неё нет)
        public static Action<string> Vozvrat(string id)
        {
            switch (id)
            {
                case "chastota": return old => PostavitHz(int.Parse(old));
                case "dvr": return old =>
                {
                    var c = old.Split(';');
                    Pisat(Registry.CurrentUser, DvrKlyuch, "GameDVR_Enabled", c[0]);
                    Pisat(Registry.CurrentUser, CapKlyuch, "AppCaptureEnabled", c.Length > 1 ? c[1] : "-");
                    Zhurnal.Pishi("запись Xbox возвращена (" + old + ")");
                };
                case "obsl": return old => { Pisat(Registry.LocalMachine, ObslKlyuch, "WakeUp", old); Zhurnal.Pishi("обслуживание: WakeUp → " + old); };
                case "vbs": return old => { Pisat(Registry.LocalMachine, HvciKlyuch, "Enabled", old); Zhurnal.Pishi("целостность памяти: Enabled → " + old); };
                case "msi": return old => { Pisat(Registry.LocalMachine, MsiKlyuch(), "MSISupported", old); Zhurnal.Pishi("MSI: → " + old); };
            }
            if (id.StartsWith("pitanie:")) { string exe = id.Substring(8); return old => Nvidia.Vernut(exe, old); }
            if (id.StartsWith("fastsync:")) { string exe = id.Substring(9); return old => Nvidia.VernutIgre(exe, Nvidia.VsyncId, old); }
            if (id.StartsWith("ansel:")) { string exe = id.Substring(6); return old => Nvidia.VernutIgre(exe, Nvidia.AnselId, old); }
            var v4 = Vozvrat4(id); if (v4 != null) return v4;
            var v5 = Vozvrat5(id); if (v5 != null) return v5;
            return null;
        }

        // ---------- 1. частота экрана ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

        static DEVMODE Novyy() { return new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) }; }

        // 59 Гц у Windows — это 59,94, по-человечески 60 (и 119 → 120, 143 → 144)
        public static int Pokazat(int hz) { return hz % 10 == 9 || hz % 10 == 3 && hz > 100 ? hz + 1 : hz; }

        public static int TekushayaHz()
        {
            var dm = Novyy();
            return EnumDisplaySettings(null, -1, ref dm) ? dm.dmDisplayFrequency : 60;
        }

        // самая высокая частота при том же разрешении и цвете
        static int MaxHz()
        {
            var cur = Novyy(); if (!EnumDisplaySettings(null, -1, ref cur)) return 0;
            int max = cur.dmDisplayFrequency;
            for (int i = 0; ; i++)
            {
                var dm = Novyy();
                if (!EnumDisplaySettings(null, i, ref dm)) break;
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmBitsPerPel == cur.dmBitsPerPel)
                    max = Math.Max(max, dm.dmDisplayFrequency);
            }
            return max;
        }

        static void PostavitHz(int hz)
        {
            var dm = Novyy(); EnumDisplaySettings(null, -1, ref dm);
            dm.dmDisplayFrequency = hz; dm.dmFields = 0x400000;   // DM_DISPLAYFREQUENCY
            int rc = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
            if (rc != 0) throw new IOException("Windows не приняла " + hz + " Гц (код " + rc + ")");
            Zhurnal.Pishi("частота экрана → " + hz + " Гц");
        }

        static Proverka Chastota()
        {
            int cur = TekushayaHz(), max = MaxHz();
            var p = new Proverka { Id = "chastota" };
            if (max <= cur + 1)   // 59 и 60 Гц — одно и то же (59,94), переключать незачем
            {
                p.Sost = Vid.Ok; p.Nazvanie = "Частота экрана " + Pokazat(cur) + " Гц — максимум"; p.Pochemu = "Максимум твоего монитора."; p.Korotko = Pokazat(cur) + " Гц, максимум";
                return p;
            }
            p.Sost = Vid.Problema; p.Nazvanie = "Экран работает не на полной частоте";
            p.Pochemu = "Windows часто включает монитор на 60 Гц, даже если он умеет больше. Кадры сверх частоты экрана ты не видишь.";
            p.Seychas = cur + " Гц"; p.Stanet = max + " Гц"; p.Knopka = "Поставить " + max + " Гц"; p.Korotko = cur + " Гц из " + max;
            p.Ispravit = () => { PostavitHz(max); return cur.ToString(); };
            p.Vernut = old => PostavitHz(int.Parse(old));
            return p;
        }

        // ---------- 2. запись Xbox в фоне (Game DVR) ----------
        const string DvrKlyuch = @"System\GameConfigStore", CapKlyuch = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

        static object Chitat(RegistryKey baza, string put, string imya)
        {
            using (var k = baza.OpenSubKey(put)) return k == null ? null : k.GetValue(imya);
        }

        static void Pisat(RegistryKey baza, string put, string imya, string staroe)
        {
            using (var k = baza.CreateSubKey(put))
            {
                if (staroe == "-") k.DeleteValue(imya, false);
                else k.SetValue(imya, int.Parse(staroe), RegistryValueKind.DWord);
            }
        }

        static Proverka Dvr()
        {
            var p = new Proverka { Id = "dvr" };
            object pol = Chitat(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
            object vkl = Chitat(Registry.CurrentUser, DvrKlyuch, "GameDVR_Enabled");
            bool vklyuchena = !(pol is int && (int)pol == 0) && !(vkl is int && (int)vkl == 0);
            if (!vklyuchena)
            {
                p.Sost = Vid.Ok; p.Nazvanie = "Запись Xbox в фоне выключена"; p.Pochemu = "Game DVR не держит запись игры наготове."; p.Korotko = "выключена";
                return p;
            }
            p.Sost = Vid.Problema; p.Nazvanie = "Запись Xbox в фоне";
            p.Pochemu = "Game DVR держит запись игры наготове и тратит на это видеокарту и диск. Если не пишешь клипы через Win+Alt+G — выключить без потерь.";
            p.Seychas = "включена"; p.Stanet = "выключена"; p.Knopka = "Выключить"; p.Korotko = "включена";
            p.Ispravit = () =>
            {
                object a = Chitat(Registry.CurrentUser, DvrKlyuch, "GameDVR_Enabled"), b = Chitat(Registry.CurrentUser, CapKlyuch, "AppCaptureEnabled");
                string old = (a is int ? a.ToString() : "-") + ";" + (b is int ? b.ToString() : "-");
                Pisat(Registry.CurrentUser, DvrKlyuch, "GameDVR_Enabled", "0");
                Pisat(Registry.CurrentUser, CapKlyuch, "AppCaptureEnabled", "0");
                Zhurnal.Pishi("запись Xbox выключена (было " + old + ")");
                return old;
            };
            p.Vernut = old =>
            {
                var c = old.Split(';');
                Pisat(Registry.CurrentUser, DvrKlyuch, "GameDVR_Enabled", c[0]);
                Pisat(Registry.CurrentUser, CapKlyuch, "AppCaptureEnabled", c.Length > 1 ? c[1] : "-");
                Zhurnal.Pishi("запись Xbox возвращена (" + old + ")");
            };
            return p;
        }

        // ---------- 3. обслуживание Windows будит ПК ----------
        const string ObslKlyuch = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";

        static Proverka Obsluzhivanie()
        {
            var p = new Proverka { Id = "obsl" };
            object w = Chitat(Registry.LocalMachine, ObslKlyuch, "WakeUp");
            if (!(w is int && (int)w == 1))
            {
                p.Sost = Vid.Ok; p.Nazvanie = "Обслуживание Windows не будит ПК"; p.Pochemu = "Оно идёт, только когда компьютер простаивает."; p.Korotko = "не будит ПК";
                return p;
            }
            p.Sost = Vid.Problema; p.Nazvanie = "Обслуживание Windows будит ПК";
            p.Pochemu = "Windows будит компьютер ночью ради обслуживания. Если ПК спал — она догоняет днём и может совпасть с каткой: FPS проседает на несколько минут.";
            p.Seychas = "будит ПК"; p.Stanet = "ждёт простоя"; p.Knopka = "Не будить"; p.Korotko = "будит ПК";
            p.Ispravit = () => { Pisat(Registry.LocalMachine, ObslKlyuch, "WakeUp", "0"); Zhurnal.Pishi("обслуживание: WakeUp 1 → 0"); return "1"; };
            p.Vernut = old => { Pisat(Registry.LocalMachine, ObslKlyuch, "WakeUp", old); Zhurnal.Pishi("обслуживание: WakeUp → " + old); };
            return p;
        }

        // ---------- 4. как игра выводит кадры (по замеру) ----------
        static Proverka Vyvod(KatkaItog k)
        {
            var p = new Proverka { Id = "vyvod", Nazvanie = "Вывод кадров" };
            if (k == null) { p.Sost = Vid.NetDannyh; p.Pochemu = "Узнаю после матча."; p.Korotko = "нужен замер"; return p; }
            if (k.CherezProc >= 50)
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Кадры идут через Windows";
                p.Pochemu = "Игра выводит кадры через Windows — так бывает в оконном режиме или когда поверх висит оверлей. Это добавляет задержку и режет FPS.";
                p.Seychas = k.CherezProc + "% кадров"; p.Stanet = "полный экран, без оверлеев"; p.Korotko = "через Windows " + k.CherezProc + "%";
                return p;
            }
            if (k.CherezProc >= 5)
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Иногда поверх игры был оверлей";
                p.Pochemu = "Часть кадров шла через Windows: поверх игры появлялось окно или оверлей (уведомление, счётчик FPS, Discord).";
                p.Seychas = k.CherezProc + "% кадров"; p.Stanet = "оверлеи выключены"; p.Korotko = "через Windows " + k.CherezProc + "%";
                return p;
            }
            p.Sost = Vid.Ok; p.Nazvanie = "Вывод кадров напрямую"; p.Pochemu = "Лучший режим: кадры идут на экран без Windows посередине."; p.Korotko = "напрямую";
            return p;
        }

        // ---------- 5. оверлей NVIDIA ----------
        static Proverka OverleyNvidia()
        {
            var p = new Proverka { Id = "nvoverlay" };
            if (Process.GetProcessesByName("NVIDIA Overlay").Length == 0)
            {
                p.Sost = Vid.Ok; p.Nazvanie = "Оверлей NVIDIA не запущен"; p.Korotko = "не запущен";
                return p;
            }
            p.Sost = Vid.Sovet; p.Nazvanie = "Оверлей NVIDIA в фоне";
            p.Pochemu = "NVIDIA App держит оверлей всё время: он следит за играми и иногда встаёт поверх. Если не пишешь клипы через Alt+Z — выключи его: NVIDIA App → Настройки → Функции → «Оверлей».";
            p.Seychas = "запущен"; p.Stanet = "выключен"; p.Korotko = "запущен";
            string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe");
            if (File.Exists(app)) { p.DeystvieTekst = "Открыть NVIDIA App"; p.Deystvie = () => Process.Start(app); }
            return p;
        }

        // ---------- 6. софт мыши и клавиатуры ----------
        static readonly string[,] Soft = { { "lghub", "Logitech G Hub" }, { "lghub_agent", "Logitech G Hub" }, { "RazerAppEngine", "Razer Synapse" },
            { "Razer Synapse 3", "Razer Synapse" }, { "iCUE", "Corsair iCUE" }, { "SteelSeriesGG", "SteelSeries GG" }, { "SteelSeriesEngine", "SteelSeries GG" },
            { "ArmouryCrate", "Armoury Crate" }, { "ArmouryCrate.UserSessionHelper", "Armoury Crate" }, { "BloodyX", "Bloody" }, { "HyperXNGenuity", "HyperX NGENUITY" } };

        static Proverka Periferiya()
        {
            var p = new Proverka { Id = "periferiya" };
            var est = new List<string>();
            for (int i = 0; i < Soft.GetLength(0); i++)
                if (Process.GetProcessesByName(Soft[i, 0]).Length > 0 && !est.Contains(Soft[i, 1])) est.Add(Soft[i, 1]);
            if (est.Count == 0) { p.Sost = Vid.Ok; p.Nazvanie = "Софта мыши в фоне нет"; p.Korotko = "нет"; return p; }
            p.Sost = Vid.Sovet; p.Nazvanie = "Софт мыши и клавиатуры в фоне";
            p.Pochemu = string.Join(", ", est) + " держит службы и оверлеи всё время. Настрой мышь один раз (DPI, частота опроса) — она запомнит — и убери программу из автозагрузки.";
            p.Seychas = string.Join(", ", est); p.Stanet = "не запускается с Windows"; p.Korotko = string.Join(", ", est);
            p.DeystvieTekst = "Открыть автозагрузку"; p.Deystvie = () => Process.Start("taskmgr.exe", "/0 /startup");
            return p;
        }

        // ---------- 7. на каком диске лежит игра ----------
        public static string TipDiska(string put)
        {
            try
            {
                if (string.IsNullOrEmpty(put) || put.Length < 2 || put[1] != ':') return null;
                char bukva = char.ToUpperInvariant(put[0]);
                var sc = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                string nomer = null;
                using (var s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                    foreach (ManagementObject o in s.Get())
                        if (o["DriveLetter"] != null && Convert.ToChar(o["DriveLetter"]) == bukva) nomer = o["DiskNumber"].ToString();
                if (nomer == null) return null;
                using (var s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject o in s.Get())
                        if (o["DeviceId"].ToString() == nomer)
                        {
                            int mt = Convert.ToInt32(o["MediaType"]), bt = Convert.ToInt32(o["BusType"]);
                            return mt == 3 ? "HDD" : mt == 4 ? (bt == 17 ? "SSD NVMe" : "SSD") : "?";
                        }
            }
            catch (Exception ex) { Zhurnal.Pishi("тип диска не узнать: " + ex.Message); }
            return null;
        }

        static Proverka DiskIgry(KatkaItog k)
        {
            var p = new Proverka { Id = "disk", Nazvanie = "Диск с игрой" };
            if (k == null || string.IsNullOrEmpty(k.ExePut)) { p.Sost = Vid.NetDannyh; p.Pochemu = "Узнаю после матча."; p.Korotko = "нужен замер"; return p; }
            string tip = TipDiska(k.ExePut);
            int sDiska = k.PoPrichinam().Where(x => x.Value.Contains("диск")).Sum(x => x.Key);
            if (tip == "HDD")
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Игра лежит на жёстком диске";
                p.Pochemu = "Игра постоянно дочитывает карту и модели с диска" + (sDiska > 0 ? " — за катку было " + sDiska + " подвисаний от этого" : "") +
                    ". На жёстком диске это в разы дольше, чем на SSD. Перенеси: Steam → игра → Свойства → Установленные файлы → Переместить.";
                p.Seychas = "HDD"; p.Stanet = "SSD"; p.Korotko = "HDD";
                return p;
            }
            p.Sost = Vid.Ok; p.Nazvanie = "Игра на " + (tip ?? "диске") + (tip != null && tip.StartsWith("SSD") ? " — быстро" : "");
            p.Pochemu = sDiska > 0 ? "Подвисаний от подгрузки с диска за катку: " + sDiska + ". Обычно их меньше со второй катки на той же карте." : "";
            p.Korotko = tip ?? "?";
            return p;
        }

        // ---------- 8. память во время катки ----------
        static Proverka Pamyat(KatkaItog k)
        {
            var p = new Proverka { Id = "pamyat", Nazvanie = "Память в игре" };
            if (k == null || k.MinSvobodnoMb < 0) { p.Sost = Vid.NetDannyh; p.Pochemu = "Узнаю после матча."; p.Korotko = "нужен замер"; return p; }
            string svob = Gb(k.MinSvobodnoMb), vsego = Gb(k.VsegoMb);
            if (k.MinSvobodnoMb < Math.Max(1500, k.VsegoMb / 10))
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Памяти в игре почти не оставалось";
                p.Pochemu = "Когда память кончается, Windows дочитывает игру с диска — отсюда подвисания. Перед игрой закрой браузер и всё лишнее.";
                p.Seychas = "свободно " + svob + " из " + vsego; p.Stanet = "закрыть лишнее перед игрой"; p.Korotko = "свободно " + svob;
                return p;
            }
            p.Sost = Vid.Ok; p.Nazvanie = "Памяти хватает"; p.Pochemu = "В игре оставалось свободно " + svob + " из " + vsego + "."; p.Korotko = "свободно " + svob;
            return p;
        }

        static string Gb(int mb) { return Itog.Chislo(mb / 1024.0) + " ГБ"; }

        // ================= волна 2 =================

        // ---------- 9. питание видеокарты для этой игры (профиль NVIDIA) ----------
        static Proverka Pitanie(Game g)
        {
            if (g == null || g.Exes.Count == 0 || !Nvidia.Est) return null;
            string exe = g.Exes[0];
            var p = new Proverka { Id = "pitanie:" + exe };
            if (Nvidia.MaxPitanie(exe))
            {
                p.Sost = Vid.Ok; p.Nazvanie = "Видеокарта на полной частоте в " + g.Name; p.Korotko = "максимальная производительность";
                p.Pochemu = "В профиле NVIDIA для этой игры стоит «максимальная производительность».";
                return p;
            }
            p.Sost = Vid.Problema; p.Nazvanie = "Видеокарта экономит энергию в " + g.Name;
            p.Pochemu = "Под лимитом кадров или в лёгкой сцене видеокарта сбрасывает частоту — кадр готовится дольше, растёт задержка. " +
                "Режим «максимальная производительность» ставлю только для этой игры, не для всего ПК. Цена — видеокарта чуть теплее в игре. " +
                "Если в игре включён Reflex «Вкл + Boost» — это уже то же самое.";
            p.Seychas = "экономия"; p.Stanet = "максимальная"; p.Knopka = "Включить для " + g.Name; p.Korotko = "экономия (по умолчанию)";
            p.Ispravit = () => Nvidia.PostavitMax(exe, g.Name);
            p.Vernut = old => Nvidia.Vernut(exe, old);
            return p;
        }

        // ---------- 10. во что упирается игра (по замеру) ----------
        static Proverka Upor(KatkaItog k, Game g)
        {
            var p = new Proverka { Id = "upor", Nazvanie = "Во что упирается игра" };
            if (k == null || k.UporGpu < 0) { p.Sost = Vid.NetDannyh; p.Pochemu = "Узнаю после матча."; p.Korotko = "нужен замер"; return p; }
            if (k.UporGpu >= 60)
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Упор в видеокарту";
                p.Pochemu = "В " + k.UporGpu + "% кадров видеокарта не успевала, а процессор её ждал. Больше FPS дадут графические настройки: тени, сглаживание, " +
                    "качество эффектов, разрешение или DLSS «Качество». Процессорные настройки (дальность прорисовки) почти ничего не дадут.";
                p.Seychas = "видеокарта " + k.UporGpu + "% кадров"; p.Stanet = "графика ниже"; p.Korotko = "видеокарта, " + k.UporGpu + "% кадров";
                return p;
            }
            if (k.UporGpu <= 25)
            {
                p.Sost = Vid.Sovet; p.Nazvanie = "Упор в процессор";
                p.Pochemu = "Видеокарта успевает и ждёт процессор (не успевала лишь в " + k.UporGpu + "% кадров). Графику можно поднять почти бесплатно, " +
                    "а FPS прибавят процессорные настройки: дальность прорисовки, количество объектов и игроков на экране, физика. " +
                    (g != null && g.Name == "CS2" ? "В CS2 это обычное дело — она почти всегда упирается в процессор. " : "") +
                    "Цифра бывает занижена, если в игре включён Reflex.";
                p.Seychas = "процессор"; p.Stanet = "процессорные настройки ниже"; p.Korotko = "процессор, видеокарта " + k.UporGpu + "%";
                return p;
            }
            p.Sost = Vid.Ok; p.Nazvanie = "Процессор и видеокарта нагружены ровно"; p.Korotko = "ровно, видеокарта " + k.UporGpu + "%";
            p.Pochemu = "Видеокарта не успевала в " + k.UporGpu + "% кадров — ни во что одно игра не упирается.";
            return p;
        }

        // ---------- 11. лимит кадров RivaTuner (ровные кадры) ----------
        public static string RtssPapka()
        {
            foreach (var k in new[] { @"SOFTWARE\WOW6432Node\Unwinder\RTSS", @"SOFTWARE\Unwinder\RTSS" })
            {
                var v = Chitat(Registry.LocalMachine, k, "InstallDir") as string;
                if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
            }
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server");
            return Directory.Exists(d) ? d : null;
        }

        static int LimitRtss(string papka, string exe)
        {
            foreach (var f in new[] { exe + ".cfg", "Global" })
            {
                string put = Path.Combine(papka, "Profiles", f);
                if (!File.Exists(put)) continue;
                foreach (var line in File.ReadAllLines(put))
                    if (line.StartsWith("Limit=")) { int n; if (int.TryParse(line.Substring(6).Trim(), out n) && n > 0) return n; break; }
            }
            return 0;
        }

        static Proverka Limit(Game g, KatkaItog k)
        {
            if (g == null || g.Exes.Count == 0) return null;
            var p = new Proverka { Id = "limit" };
            string papka = RtssPapka();
            int lim = papka == null ? 0 : LimitRtss(papka, g.Exes[0]);
            if (lim > 0) { p.Sost = Vid.Ok; p.Nazvanie = "Лимит кадров RivaTuner: " + lim + " FPS"; p.Korotko = lim + " FPS"; p.Pochemu = "Кадры идут с ровным шагом."; return p; }
            p.Sost = Vid.Sovet; p.Nazvanie = "Ровные кадры: лимита нет";
            p.Pochemu = "Без лимита FPS скачет, и глаз видит рывки даже при высоком среднем. Лимит RivaTuner чуть ниже того, что игра держит постоянно, " +
                "даёт ровный шаг кадров: в Deadlock 1% low вырос на 30% (замер PresentMon). Поставить в один клик — бесплатная программа «Ровный FPS».";
            p.Seychas = "без лимита"; p.Stanet = k != null && k.Derzhit > 30 ? "лимит около " + ((int)(k.Derzhit * 0.95) / 5 * 5) + " FPS" : "лимит чуть ниже FPS в бою";
            p.Korotko = papka == null ? "RivaTuner не установлен" : "лимита нет";
            p.DeystvieTekst = "Скачать «Ровный FPS»"; p.Deystvie = () => Process.Start("https://github.com/mrraketa/rovny-fps#readme");
            return p;
        }

        // ================= волна 3 =================

        // ---------- 12. целостность памяти (VBS/HVCI) — с ценой ----------
        const string HvciKlyuch = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

        static Proverka Vbs()
        {
            var p = new Proverka { Id = "vbs" };
            bool hvci = false;
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard"), new ObjectQuery("SELECT SecurityServicesRunning FROM Win32_DeviceGuard")))
                    foreach (ManagementObject o in s.Get())
                    {
                        var run = o["SecurityServicesRunning"] as uint[];
                        if (run != null && run.Contains(2u)) hvci = true;
                    }
            }
            catch (Exception ex) { Zhurnal.Pishi("целостность памяти не узнать: " + ex.Message); return null; }
            if (!hvci) { p.Sost = Vid.Ok; p.Nazvanie = "Целостность памяти выключена"; p.Korotko = "выключена"; p.Pochemu = "Защита ядра не отнимает процессор."; return p; }
            p.Sost = Vid.Risk; p.Nazvanie = "Целостность памяти (VBS)";
            p.Pochemu = "Защита ядра Windows на виртуализации. Когда игра упирается в процессор, она съедает до 6–7% FPS (CS2 на низких настройках); " +
                "в играх, которые упираются в видеокарту, разницы почти нет. Выключение действует после перезагрузки.";
            p.Seychas = "включена"; p.Stanet = "выключена"; p.Knopka = "Выключить"; p.Korotko = "включена · риск, до +7%";
            p.Cena = "Слабее защита от вредоносных драйверов. Подумай, если ставишь программы из сомнительных мест. Вернуть можно здесь же, кнопкой «вернуть». Нужна перезагрузка.";
            p.Ispravit = () =>
            {
                object o = Chitat(Registry.LocalMachine, HvciKlyuch, "Enabled");
                string old = o is int ? o.ToString() : "-";
                Pisat(Registry.LocalMachine, HvciKlyuch, "Enabled", "0");
                Zhurnal.Pishi("целостность памяти: Enabled " + old + " → 0 (после перезагрузки)");
                return old;
            };
            return p;
        }

        // ---------- 13. драйвер видеокарты ----------
        static Proverka Drayver()
        {
            var p = new Proverka { Id = "drayver" };
            DateTime data = DateTime.MinValue;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DriverDate FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                        if ((o["Name"] ?? "").ToString().Contains("NVIDIA") && o["DriverDate"] != null)
                            data = ManagementDateTimeConverter.ToDateTime(o["DriverDate"].ToString());
            }
            catch (Exception) { }
            if (data == DateTime.MinValue) return null;
            int dney = (int)(DateTime.Now - data).TotalDays;
            string ver = Zhelezo.Drayver + " от " + data.ToString("dd.MM.yyyy");
            if (dney <= 150) { p.Sost = Vid.Ok; p.Nazvanie = "Драйвер свежий: " + ver; p.Korotko = ver; return p; }
            p.Sost = Vid.Sovet; p.Nazvanie = "Драйвер видеокарты старый";
            p.Pochemu = "Драйверу " + dney + " дней. Новые исправляют ошибки в играх (на Windows 11 24H2 старые ломали полноэкранный режим). " +
                "Скачай с сайта NVIDIA и при установке выбери «Выборочная» → только драйвер, без NVIDIA App и HD Audio, если звук не через монитор.";
            p.Seychas = ver; p.Stanet = "свежий, только драйвер"; p.Korotko = "старый, " + ver;
            p.DeystvieTekst = "Открыть сайт NVIDIA"; p.Deystvie = () => Process.Start("https://www.nvidia.com/ru-ru/drivers/");
            return p;
        }

        // ---------- 14. Resizable BAR (включается в BIOS — только инструкция) ----------
        [DllImport("kernel32.dll")] static extern bool GetFirmwareType(out int t);
        static string rebarKesh;

        static Proverka Rebar()
        {
            if (rebarKesh == null)
            {
                rebarKesh = "";
                try
                {
                    string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
                    if (File.Exists(smi))
                        using (var pr = Process.Start(new ProcessStartInfo(smi, "-q -d MEMORY") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }))
                        {
                            rebarKesh = pr.StandardOutput.ReadToEnd();
                            pr.WaitForExit(5000);
                        }
                }
                catch (Exception ex) { Zhurnal.Pishi("nvidia-smi: " + ex.Message); }
            }
            int fb = SmiTotal(rebarKesh, "FB Memory Usage"), bar = SmiTotal(rebarKesh, "BAR1 Memory Usage");
            if (fb <= 0 || bar <= 0) return null;
            var p = new Proverka { Id = "rebar" };
            if (bar > 512) { p.Sost = Vid.Ok; p.Nazvanie = "Resizable BAR включён"; p.Korotko = "включён (" + bar + " МБ)"; return p; }
            int ft; bool uefi = GetFirmwareType(out ft) && ft == 2;
            p.Sost = Vid.Sovet; p.Nazvanie = "Resizable BAR выключен";
            p.Pochemu = "Процессору видна только " + bar + " МБ памяти видеокарты из " + Itog.Chislo(fb / 1024.0) + " ГБ. С Resizable BAR — вся сразу. " +
                "На NVIDIA это обычно пара кадров, в отдельных играх больше. Включается в BIOS: Above 4G Decoding — Enabled, Re-Size BAR Support — Auto, CSM — Disabled. " +
                (uefi ? "Windows у тебя в режиме UEFI — выключать CSM можно." : "Внимание: Windows стоит в старом режиме BIOS (не UEFI) — после выключения CSM она не загрузится. Сначала нужен перевод диска в GPT, это отдельная работа.");
            p.Seychas = bar + " МБ"; p.Stanet = "вся память, в BIOS"; p.Korotko = "выключен (" + bar + " МБ)";
            return p;
        }

        static int SmiTotal(string t, string razdel)
        {
            int i = (t ?? "").IndexOf(razdel); if (i < 0) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(t.Substring(i), @"Total\s*:\s*(\d+)\s*MiB");
            int n; return m.Success && int.TryParse(m.Groups[1].Value, out n) ? n : 0;
        }

        // ---------- 15. прерывания видеокарты (MSI) ----------
        static string msiKlyuch;
        static string MsiKlyuch()
        {
            if (msiKlyuch != null) return msiKlyuch;
            msiKlyuch = "";
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                        if ((o["Name"] ?? "").ToString().Contains("NVIDIA"))
                            msiKlyuch = @"SYSTEM\CurrentControlSet\Enum\" + o["PNPDeviceID"] + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
            }
            catch (Exception) { }
            return msiKlyuch;
        }

        static Proverka Msi()
        {
            string k = MsiKlyuch(); if (k == "") return null;
            object v = Chitat(Registry.LocalMachine, k, "MSISupported");
            var p = new Proverka { Id = "msi" };
            if (v is int && (int)v == 1) { p.Sost = Vid.Ok; p.Nazvanie = "Прерывания видеокарты — MSI"; p.Korotko = "MSI"; return p; }
            p.Sost = Vid.Problema; p.Nazvanie = "Прерывания видеокарты по-старому";
            p.Pochemu = "Видеокарта получает прерывания старым способом (линией), а не сообщениями MSI — это лишняя задержка. На RTX 30 и новее MSI обычно уже включён. Действует после перезагрузки.";
            p.Seychas = "линия"; p.Stanet = "MSI"; p.Knopka = "Включить MSI"; p.Korotko = "линия";
            p.Ispravit = () => { string old = v is int ? v.ToString() : "-"; Pisat(Registry.LocalMachine, k, "MSISupported", "1"); Zhurnal.Pishi("MSI: " + old + " → 1"); return old; };
            return p;
        }

    }

    // Железо для шапки и отчёта
    static class Zhelezo
    {
        public static string Cpu = "?", Gpu = "?", Drayver = "", Windows = "";
        public static int OzuGb;
        public static volatile bool Gotovo;   // Uznat отработал (в окне он идёт в фоне)

        public static void Uznat()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get())
                        Cpu = System.Text.RegularExpressions.Regex.Replace(o["Name"].ToString(), @"\s+\d+-Core Processor|\(R\)|\(TM\)|CPU|@.*$|\s{2,}", " ").Trim();
            }
            catch (Exception ex) { Zhurnal.Pishi("процессор не узнать: " + ex.Message); }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string n = (o["Name"] ?? "").ToString();
                        if (n.Contains("Virtual") || n.Contains("Basic") || n.Contains("Remote")) continue;
                        if (Gpu != "?" && !n.Contains("NVIDIA")) continue;
                        Gpu = n.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
                        Drayver = NvidiaVersiya((o["DriverVersion"] ?? "").ToString());
                    }
            }
            catch (Exception ex) { Zhurnal.Pishi("видеокарту не узнать: " + ex.Message); }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) OzuGb = (int)Math.Round(Convert.ToDouble(o["TotalPhysicalMemory"]) / 1073741824.0);
            }
            catch (Exception) { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse((k.GetValue("CurrentBuild") ?? "0").ToString(), out build);
                    Windows = "Windows " + (build >= 22000 ? "11" : "10") + " " + (k.GetValue("DisplayVersion") ?? "");
                }
            }
            catch (Exception) { }
            Zhurnal.Pishi("железо: " + Cpu + " · " + Gpu + " (драйвер " + Drayver + ") · " + OzuGb + " ГБ · " + Windows);
            Gotovo = true;
        }

        // 32.0.16.2101 → 621.01 (последние 5 цифр — номер NVIDIA)
        static string NvidiaVersiya(string v)
        {
            var c = v.Replace(".", "");
            if (c.Length < 5) return v;
            c = c.Substring(c.Length - 5);
            return c.Substring(0, 3) + "." + c.Substring(3);
        }
    }

    // Точка восстановления Windows — отдельно от точек отката программы и только по кнопке:
    // программа сама её никогда не создаёт.
    static class TochkaWindows
    {
        const string Put = @"\\.\root\default";

        // есть ли у Windows нужный метод — без создания точки (для самопроверки)
        public static bool MetodEst()
        {
            try
            {
                using (var cls = new ManagementClass(new ManagementScope(Put), new ManagementPath("SystemRestore"), null))
                using (var par = cls.GetMethodParameters("CreateRestorePoint"))
                    return par.Properties["Description"] != null && par.Properties["RestorePointType"] != null && par.Properties["EventType"] != null;
            }
            catch (Exception) { return false; }
        }

        // null — создана; иначе причина человеческими словами. opisanie должно быть уникальным (с датой и временем).
        public static string Sozdat(string opisanie)
        {
            try
            {
                var sc = new ManagementScope(Put);
                using (var cls = new ManagementClass(sc, new ManagementPath("SystemRestore"), null))
                using (var par = cls.GetMethodParameters("CreateRestorePoint"))
                {
                    par["Description"] = opisanie; par["RestorePointType"] = 12 /* MODIFY_SETTINGS */; par["EventType"] = 100 /* BEGIN_SYSTEM_CHANGE */;
                    using (var res = cls.InvokeMethod("CreateRestorePoint", par, null))
                    {
                        int rc = Convert.ToInt32(res["ReturnValue"]);
                        if (rc == 1058) return "в Windows выключена «Защита системы» для диска C:";
                        if (rc != 0) return "Windows ответила кодом " + rc;
                    }
                }
                // Windows молча пропускает создание, если за последние сутки уже была точка, — проверяем, что наша появилась
                using (var se = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT Description FROM SystemRestore")))
                    foreach (ManagementObject o in se.Get())
                        if ((o["Description"] ?? "").ToString() == opisanie) return null;
                return "Windows её пропустила: за последние сутки уже создавалась другая точка восстановления";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
