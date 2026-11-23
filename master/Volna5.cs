// Volna5.cs — пятая волна: следы твикеров (методичка §3.5). «Ускорители» и чужие гайды оставляют значения, которых
// у Windows и драйвера по умолчанию нет; пользы по замерам у них нет, а вред бывает. Нахожу — и откатываю ТОЛЬКО по кнопке,
// с запоминанием прежнего значения и кнопкой «вернуть». Карточка появляется, только если след найден (или мы его уже убрали).
// Не вошло: CPMINCORES=100 — на ПК это значение Windows по умолчанию (проверено 09.11 на схеме «Сбалансированная»), не след.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FpsMaster
{
    static partial class Proverki
    {
        const string MmKlyuch = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        const string PodkachkaKlyuch = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        const string IfeoKlyuch = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string LayersKlyuch = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string FsoMetka = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        static readonly string[] NvTviki = { "DisableDynamicPstate", "PerfLevelSrc", "PowerMizerEnable", "PowerMizerLevel", "PowerMizerLevelAC",
            "DisablePreemption", "DisableCudaContextPreemption", "EnableCEPreemption", "DisablePreemptionOnS3S4", "ComputePreemption" };
        static readonly string[] BcdTviki = { "useplatformclock", "useplatformtick", "disabledynamictick" };

        // ---------- одна запись реестра (DWORD): «чужое» значение → откат на значение Windows ("-" — удалить запись) ----------
        class RegSled { public string Id, Klyuch, Imya, Otkat, Nazvanie, Pochemu, Stanet, Cena; public Func<int, bool> Chuzhoe; public bool Sovet; }

        static readonly RegSled[] RegSledy =
        {
            new RegSled { Id = "tv:sysresp", Klyuch = MmKlyuch, Imya = "SystemResponsiveness", Otkat = "20", Chuzhoe = v => v > 20,
                Nazvanie = "Звук без приоритета в играх", Stanet = "20, как в Windows",
                Pochemu = "Значение выше 20 ставят «ускорители»: Windows отдаёт больше процессора фоновым задачам, а звук и видео теряют приоритет — " +
                          "в игре возможен треск звука. Пользы для FPS нет. По умолчанию 20." },
            new RegSled { Id = "tv:netthrot", Klyuch = MmKlyuch, Imya = "NetworkThrottlingIndex", Otkat = "10", Chuzhoe = v => v != 10, Sovet = true,
                Nazvanie = "Снят лимит сетевых пакетов", Stanet = "можно оставить",
                Pochemu = "Пока играет звук или видео, Windows ограничивает сеть примерно до 100 Мбит/с, чтобы звук не хрипел; это значение лимит снимает. " +
                          "На игры не влияет — они шлют мало пакетов. Если интернет быстрее 100 Мбит/с и качаешь под музыку, может даже помогать; " +
                          "редко звук хрипит при очень быстрой загрузке — тогда верни 10. Трогать не обязательно." },
            new RegSled { Id = "tv:prio", Klyuch = @"SYSTEM\CurrentControlSet\Control\PriorityControl", Imya = "Win32PrioritySeparation", Otkat = "2", Chuzhoe = v => v != 2,
                Nazvanie = "Изменён планировщик процессора", Stanet = "2, как в Windows",
                Pochemu = "«Магического» значения нет: замеры пользы не показали, а часть значений (длинные кванты) делает хуже фон и Alt+Tab. " +
                          "Вернуть стандартное — чтобы система вела себя как у всех. По умолчанию 2." },
            new RegSled { Id = "tv:vdb", Klyuch = @"SYSTEM\CurrentControlSet\Control\CI\Config", Imya = "VulnerableDriverBlocklistEnable", Otkat = "1", Chuzhoe = v => v == 0,
                Nazvanie = "Выключен список опасных драйверов", Stanet = "включён, как в Windows 11",
                Pochemu = "К FPS он отношения не имеет — его выключают «на всякий случай» или ради старых утилит. Через уязвимые драйверы " +
                          "заходят вирусы и читы. Действует после перезагрузки.",
                Cena = "Драйверы из списка Microsoft (старые утилиты разгона и мониторинга) перестанут загружаться — их нужно будет обновить." },
            new RegSled { Id = "tv:mpo", Klyuch = @"SOFTWARE\Microsoft\Windows\Dwm", Imya = "OverlayTestMode", Otkat = "-", Chuzhoe = v => v == 5,
                Nazvanie = "Выключены оверлеи MPO", Stanet = "как в Windows",
                Pochemu = "Так советовали в 2022 году против мерцания на части драйверов. FPS это не добавляет, а окно без рамки и видео поверх игры " +
                          "идут через лишнее копирование. Если ставил из-за мерцания и оно было — нажми «Пропустить». Действует после перезагрузки." },
            new RegSled { Id = "tv:timer", Klyuch = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", Imya = "GlobalTimerResolutionRequests", Otkat = "-", Chuzhoe = v => v == 1,
                Nazvanie = "Таймер ускорен для всех программ", Stanet = "как в Windows 11",
                Pochemu = "Любая программа может поднять частоту системного таймера для всей системы (как было до Windows 11). Пользы для FPS " +
                          "по замерам нет, а процессор реже засыпает: выше нагрев, у ноутбука быстрее садится батарея. Действует после перезагрузки." },
        };

        static string Hex(int v) { return v >= 0 && v < 10 ? v.ToString() : "0x" + ((uint)v).ToString("x") + (v > 0 ? " (" + v + ")" : ""); }

        static Proverka Sled(string id, string nazvanie, string pochemu, string seychas, string stanet)
        {
            return new Proverka { Id = id, Sost = Vid.Problema, Nazvanie = nazvanie, Pochemu = "След «ускорителя» или чужого гайда. " + pochemu,
                Seychas = seychas, Stanet = stanet, Knopka = "Откатить", Korotko = "след твикера" };
        }

        // След уже убран нами — карточка «в порядке» с кнопкой «вернуть»; иначе карточки нет
        static Proverka Ubrano(string id, string nazvanie, Dictionary<string, string> fixy)
        {
            return fixy.ContainsKey(id) ? new Proverka { Id = id, Sost = Vid.Ok, Nazvanie = "Откачено: " + nazvanie, Korotko = "откачено" } : null;
        }

        static Proverka RegProverka(RegSled s, Dictionary<string, string> fixy)
        {
            object v = Chitat(Registry.LocalMachine, s.Klyuch, s.Imya);
            if (!(v is int) || !s.Chuzhoe((int)v)) return Ubrano(s.Id, s.Nazvanie, fixy);
            int bylo = (int)v;
            var p = Sled(s.Id, s.Nazvanie, s.Pochemu, s.Imya + " = " + Hex(bylo), s.Stanet);
            if (s.Sovet) { p.Sost = Vid.Sovet; p.Korotko = "след твикера, не вредит"; return p; }
            if (s.Cena != null) { p.Sost = Vid.Risk; p.Cena = s.Cena; p.Korotko = "след твикера · риск"; }
            p.Ispravit = () => { Pisat(Registry.LocalMachine, s.Klyuch, s.Imya, s.Otkat); Zhurnal.Pishi(s.Imya + ": " + Hex(bylo) + " → " + s.Otkat); return bylo.ToString(); };
            return p;
        }

        // ---------- запуск системных утилит (bcdedit, powercfg) ----------
        static string Komanda(string exe, string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var pr = Process.Start(psi))
            {
                var err = pr.StandardError.ReadToEndAsync();   // оба потока сразу: иначе большой вывод ошибок повесит чтение
                string o = pr.StandardOutput.ReadToEnd(), e = err.Result;
                pr.WaitForExit(15000);
                if (pr.ExitCode != 0) throw new InvalidOperationException(exe + " " + args + ": " + (o + " " + e).Trim());
                return o;
            }
        }

        // ---------- bcdedit: принудительный HPET и отключённый динамический тик ----------
        public static List<string> BcdRazobrat(string vyvod)
        {
            var r = new List<string>();
            foreach (var line in vyvod.Split('\n'))
            {
                var t = line.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) continue;
                string imya = t[0].ToLowerInvariant(), zn = t[1].Trim().ToLowerInvariant();
                if (BcdTviki.Contains(imya) && zn != "no" && zn != "нет" && !r.Contains(imya)) r.Add(imya);
            }
            return r;
        }

        static Proverka Bcd(Dictionary<string, string> fixy)
        {
            const string id = "tv:bcd", naz = "HPET или тик в загрузке Windows";
            var est = BcdRazobrat(Komanda("bcdedit.exe", "/enum {current}"));
            if (est.Count == 0) return Ubrano(id, naz, fixy);
            var p = Sled(id, naz, "Твикеры прописывают в загрузку Windows " + string.Join(", ", est) + ". Принудительный HPET (useplatformclock) " +
                "в замерах снижал FPS и добавлял задержку; отключённый динамический тик только мешает процессору засыпать. По умолчанию их нет. " +
                "Действует после перезагрузки.", string.Join(", ", est), "как в Windows");
            p.Sost = Vid.Risk; p.Korotko = "след твикера · риск";
            p.Cena = "Нужна перезагрузка. Если диск C: зашифрован BitLocker, Windows может один раз попросить ключ восстановления — найди его заранее " +
                     "(account.microsoft.com/devices/recoverykey).";
            p.Ispravit = () =>
            {
                var ubrano = new List<string>();   // запоминаем только то, что правда убрали: иначе «вернуть» не вернёт
                foreach (var n in est)
                    try { Komanda("bcdedit.exe", "/deletevalue {current} " + n); ubrano.Add(n); }
                    catch (Exception ex) { Zhurnal.Pishi("bcdedit: " + n + " не убрано: " + ex.Message); }
                if (ubrano.Count == 0) throw new InvalidOperationException("Windows не дала изменить загрузку");
                Zhurnal.Pishi("bcdedit: убрано " + string.Join(", ", ubrano));
                return string.Join("|", ubrano);
            };
            return p;
        }

        // ---------- схема питания: простой процессора выключен ----------
        public static int[] IdleRazobrat(string vyvod)
        {
            var m = Regex.Matches(vyvod, "0x([0-9a-fA-F]{8})");
            if (m.Count < 2) return null;
            return new[] { Convert.ToInt32(m[m.Count - 2].Groups[1].Value, 16), Convert.ToInt32(m[m.Count - 1].Groups[1].Value, 16) };   // от сети, от батареи
        }

        static void PostavitIdle(int ac, int dc)
        {
            Komanda("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE " + ac);
            Komanda("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE " + dc);
            Komanda("powercfg.exe", "/setactive SCHEME_CURRENT");
            Zhurnal.Pishi("питание: IDLEDISABLE → " + ac + "/" + dc);
        }

        static Proverka Idle(Dictionary<string, string> fixy)
        {
            const string id = "tv:idle", naz = "Процессор не простаивает";
            var v = IdleRazobrat(Komanda("powercfg.exe", "/qh SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE"));
            if (v == null || (v[0] != 1 && v[1] != 1)) return Ubrano(id, naz, fixy);
            var p = Sled(id, naz, "В схеме питания выключен простой процессора: ядра не засыпают никогда. Так советуют ради задержки, но процессор " +
                "заметно сильнее греется и может раньше сбрасывать частоту под нагрузкой — на Ryzen и в ноутбуках это минус FPS. Замеров пользы нет. " +
                "По умолчанию простой включён.", "простой выключен", "простой включён");
            int ac = v[0], dc = v[1];
            p.Ispravit = () => { PostavitIdle(0, 0); return ac + ";" + dc; };
            return p;
        }

        // ---------- высокий приоритет процесса, прописанный навсегда (IFEO) ----------
        static Dictionary<string, int> IfeoPrioritety()
        {
            var r = new Dictionary<string, int>();
            using (var k = Registry.LocalMachine.OpenSubKey(IfeoKlyuch))
            {
                if (k == null) return r;
                foreach (var exe in k.GetSubKeyNames())
                    try
                    {
                        using (var po = k.OpenSubKey(exe + @"\PerfOptions"))
                        {
                            var v = po == null ? null : po.GetValue("CpuPriorityClass");
                            if (v is int && ((int)v == 3 || (int)v == 4)) r[exe] = (int)v;
                        }
                    }
                    catch (Exception) { }   // часть ключей закрыта даже администратору
            }
            return r;
        }

        static Proverka Ifeo(Dictionary<string, string> fixy)
        {
            const string id = "tv:ifeo", naz = "Высокий приоритет навсегда";
            var est = IfeoPrioritety();
            if (est.Count == 0) return Ubrano(id, naz, fixy);
            string spisok = string.Join(", ", est.Keys);
            var p = Sled(id, naz, "Для " + spisok + " в реестре навсегда задан " + (est.Values.Any(x => x == 4) ? "приоритет реального времени или высокий" : "высокий приоритет") +
                ". Замеров пользы нет, а когда игра упирается в процессор, она отбирает время у звука, мыши и Discord — отсюда треск и рывки ввода.",
                spisok, "обычный приоритет");
            p.Ispravit = () =>
            {
                foreach (var kv in est)
                    using (var po = Registry.LocalMachine.OpenSubKey(IfeoKlyuch + "\\" + kv.Key + @"\PerfOptions", true))
                        if (po != null) po.DeleteValue("CpuPriorityClass", false);
                Zhurnal.Pishi("IFEO: снят приоритет у " + spisok);
                return string.Join("|", est.Select(kv => kv.Key + ">" + kv.Value));
            };
            return p;
        }

        // ---------- файл подкачки выключен ----------
        static Proverka Podkachka(Dictionary<string, string> fixy)
        {
            const string id = "tv:podkachka", naz = "Файл подкачки выключен";
            var pf = Chitat(Registry.LocalMachine, PodkachkaKlyuch, "PagingFiles") as string[];
            if (pf == null || !pf.All(string.IsNullOrWhiteSpace)) return Ubrano(id, naz, fixy);
            var p = Sled(id, naz, "Частый «совет» ради FPS, но замеров пользы нет. Когда памяти не хватает, игра или браузер просто вылетают, " +
                "а не притормаживают. Ставлю «размер выбирает Windows». Действует после перезагрузки.", "нет", "размер выбирает Windows");
            p.Ispravit = () =>
            {
                using (var k = Registry.LocalMachine.CreateSubKey(PodkachkaKlyuch)) k.SetValue("PagingFiles", new[] { @"?:\pagefile.sys" }, RegistryValueKind.MultiString);
                Zhurnal.Pishi("файл подкачки: размер выбирает Windows");
                return "!" + string.Join("|", pf);
            };
            return p;
        }

        // ---------- «полноэкранная оптимизация» выключена у программ ----------
        // Убрать метку из строки совместимости; null — в строке больше ничего нет, запись удалить
        public static string UbratMetku(string data)
        {
            var t = data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !x.Equals(FsoMetka, StringComparison.OrdinalIgnoreCase)).ToList();
            return t.All(x => x == "~") ? null : string.Join(" ", t);
        }

        static List<string[]> FsoZapisi()   // { "U"/"M", путь, строка }
        {
            var r = new List<string[]>();
            foreach (var b in new[] { "U", "M" })
                using (var k = (b == "U" ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(LayersKlyuch))
                    if (k != null)
                        foreach (var n in k.GetValueNames())
                        {
                            var d = k.GetValue(n) as string;
                            if (d != null && d.IndexOf(FsoMetka, StringComparison.OrdinalIgnoreCase) >= 0) r.Add(new[] { b, n, d });
                        }
            return r;
        }

        static void PisatLayer(string b, string put, string data)
        {
            using (var k = (b == "U" ? Registry.CurrentUser : Registry.LocalMachine).CreateSubKey(LayersKlyuch))
                if (data == null) k.DeleteValue(put, false); else k.SetValue(put, data, RegistryValueKind.String);
        }

        static Proverka Fso(Dictionary<string, string> fixy)
        {
            const string id = "tv:fso", naz = "Выключена полноэкранная оптимизация";
            var est = FsoZapisi();
            if (est.Count == 0) return Ubrano(id, naz, fixy);
            string spisok = string.Join(", ", est.Select(x => Path.GetFileName(x[1])).Distinct());
            var p = Sled(id, naz, "У " + spisok + " в свойствах (Совместимость) выключена полноэкранная оптимизация. Совет времён Windows 10 1803, замеров пользы нет; " +
                "без неё игра не получает новый вывод кадров Windows — меньше задержка в окне без рамки, Auto HDR, быстрый Alt+Tab.", spisok, "как в Windows");
            p.Ispravit = () =>
            {
                foreach (var x in est) PisatLayer(x[0], x[1], UbratMetku(x[2]));
                Zhurnal.Pishi("совместимость: убрано " + FsoMetka + " у " + spisok);
                return string.Join("|", est.Select(x => x[0] + ">" + x[1] + ">" + x[2]));
            };
            return p;
        }

        // ---------- твики в реестре драйвера NVIDIA (KBoost, PowerMizer, вытеснение) ----------
        static Proverka NvReestr(Dictionary<string, string> fixy)
        {
            const string id = "tv:nvreg", naz = "Твики в настройках драйвера NVIDIA";
            string k = HdcpKlyuch(); if (k == "") return null;
            var est = new Dictionary<string, int>();
            foreach (var n in NvTviki) { var v = Chitat(Registry.LocalMachine, k, n); if (v is int) est[n] = (int)v; }
            if (est.Count == 0) return Ubrano(id, naz, fixy);
            string spisok = string.Join(", ", est.Keys);
            var p = Sled(id, naz, "Остались твики: " + spisok + ". Они держат видеокарту на максимальной частоте всегда (KBoost, PowerMizer) или выключают " +
                "вытеснение задач. FPS это не добавляет — в игре драйвер сам поднимает частоту, — а на рабочем столе карта греется и шумит; без вытеснения " +
                "возможны зависания и сбросы драйвера. Действует после перезагрузки.", spisok, "как после установки драйвера");
            p.Ispravit = () =>
            {
                foreach (var n in est.Keys) Pisat(Registry.LocalMachine, k, n, "-");
                Zhurnal.Pishi("драйвер NVIDIA: убрано " + spisok);
                return string.Join("|", est.Select(kv => kv.Key + ">" + kv.Value));
            };
            return p;
        }

        // ---------- старый ограничитель кадров Profile Inspector ----------
        const string FrlPochemu = "Стоит старый ограничитель кадров из NVIDIA Profile Inspector. Он добавляет 2 и больше кадров задержки. Новый ограничитель есть " +
            "в самой панели NVIDIA («Макс. частота кадров») и в RivaTuner — у них задержки почти нет.";

        static Proverka FrlGlobal(Dictionary<string, string> fixy)
        {
            const string id = "tv:frl", naz = "Старый лимит кадров во всех играх";
            if (!Nvidia.Est) return null;
            var v = Nvidia.ZnachenieGlobal(Nvidia.StaryyLimitId);
            if (!v.HasValue || v.Value == 0) return Ubrano(id, naz, fixy);
            var p = Sled(id, naz, FrlPochemu + " Сейчас он в глобальном профиле — то есть во всех играх.", "включён", "снят");
            p.Ispravit = () => Nvidia.SnyatGlobal(Nvidia.StaryyLimitId);
            return p;
        }

        static Proverka FrlIgry(Game g, Dictionary<string, string> fixy)
        {
            if (g == null || g.Exes.Count == 0 || !Nvidia.Est) return null;
            string exe = g.Exes[0], id = "tv:frl:" + exe, naz = "Старый лимит кадров в " + g.Name;
            var v = Nvidia.ZnachenieIgry(exe, Nvidia.StaryyLimitId);
            if (!v.HasValue || v.Value == 0) return Ubrano(id, naz, fixy);
            var p = Sled(id, naz, FrlPochemu, "включён", "снят");
            uint bylo = v.Value;
            p.Ispravit = () => { Nvidia.Rezerv(); Nvidia.VernutIgre(exe, Nvidia.StaryyLimitId, "-"); return bylo.ToString(); };
            return p;
        }

        // ---------- все следы разом ----------
        static List<Proverka> Tvikery(Dictionary<string, string> fixy, Game g)
        {
            var r = new List<Proverka>();
            var proverki = new List<Func<Proverka>>();
            foreach (var s in RegSledy) { var ss = s; proverki.Add(() => RegProverka(ss, fixy)); }
            proverki.Add(() => Bcd(fixy)); proverki.Add(() => Idle(fixy)); proverki.Add(() => Ifeo(fixy)); proverki.Add(() => Podkachka(fixy));
            proverki.Add(() => Fso(fixy)); proverki.Add(() => NvReestr(fixy)); proverki.Add(() => FrlGlobal(fixy)); proverki.Add(() => FrlIgry(g, fixy));
            foreach (var f in proverki)
                try { var p = f(); if (p != null) r.Add(p); }
                catch (Exception ex) { Zhurnal.Pishi("след твикера не проверить: " + ex.Message); }
            if (!r.Any(p => p.Nado))
                r.Add(new Proverka { Id = "tvikery", Sost = Vid.Ok, Nazvanie = "Следов твикеров нет", Korotko = "не найдено",
                    Pochemu = "Проверил реестр, загрузку Windows, схему питания, приоритеты, совместимость и профиль драйвера NVIDIA." });
            return r;
        }

        static Action<string> Vozvrat5(string id)
        {
            foreach (var s in RegSledy)
                if (s.Id == id) { var ss = s; return old => { Pisat(Registry.LocalMachine, ss.Klyuch, ss.Imya, old); Zhurnal.Pishi(ss.Imya + " → " + old); }; }
            switch (id)
            {
                case "tv:bcd": return old => { foreach (var n in old.Split('|')) Komanda("bcdedit.exe", "/set {current} " + n + " yes"); Zhurnal.Pishi("bcdedit: возвращено " + old); };
                case "tv:idle": return old => { var c = old.Split(';'); PostavitIdle(int.Parse(c[0]), int.Parse(c[1])); };
                case "tv:ifeo": return old =>
                {
                    foreach (var x in old.Split('|'))
                    {
                        var c = x.Split('>');
                        using (var po = Registry.LocalMachine.CreateSubKey(IfeoKlyuch + "\\" + c[0] + @"\PerfOptions")) po.SetValue("CpuPriorityClass", int.Parse(c[1]), RegistryValueKind.DWord);
                    }
                    Zhurnal.Pishi("IFEO: приоритет возвращён (" + old + ")");
                };
                case "tv:podkachka": return old =>
                {
                    using (var k = Registry.LocalMachine.CreateSubKey(PodkachkaKlyuch)) k.SetValue("PagingFiles", old.Substring(1).Split('|'), RegistryValueKind.MultiString);
                    Zhurnal.Pishi("файл подкачки: возвращено как было");
                };
                case "tv:fso": return old => { foreach (var x in old.Split('|')) { var c = x.Split('>'); PisatLayer(c[0], c[1], c[2]); } Zhurnal.Pishi("совместимость: возвращено"); };
                case "tv:nvreg": return old =>
                {
                    foreach (var x in old.Split('|')) { var c = x.Split('>'); Pisat(Registry.LocalMachine, HdcpKlyuch(), c[0], c[1]); }
                    Zhurnal.Pishi("драйвер NVIDIA: возвращено " + old);
                };
                case "tv:frl": return old => Nvidia.VernutGlobal(Nvidia.StaryyLimitId, old);
            }
            if (id.StartsWith("tv:frl:")) { string exe = id.Substring(7); return old => Nvidia.VernutIgre(exe, Nvidia.StaryyLimitId, old); }
            return null;
        }
    }
}
