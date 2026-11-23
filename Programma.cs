using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FpsMaster
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        public const int Versiya = 13;   // общая программа: ограничение FPS (v10) + настройки
        // Откуда берутся обновления (Obnovlenie.cs). С версии 13 программа открытая: тот же адрес, что у прежнего v10,
        // поэтому у кого стоит v10, кнопка «Обновить» приводит сюда же. Встроенный модуль v10 сам обновления не проверяет (Vnutri).
        public const string ManifestUrl = "https://raw.githubusercontent.com/mrraketa/rovny-fps/main/versiya.txt";
        public static string PutSostoyaniya { get { return Path.Combine(Zhurnal.Papka, "sostoyanie.txt"); } }

        [STAThread]
        static void Main(string[] args)
        {
            args = Obnovlenie.PosleZapuska(args);   // после обновления: дождаться выхода прежней копии, убрать .old
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --proba-vynos [папка] --vstroeno: проба выноса окна из оболочки (ProbaVynos.cs); запускать копией exe с другим именем.
            // Состояние — во временном файле, как у --shot: свои настройки и профили RivaTuner проба не трогает.
            if (ProbaVynos.Est(args))
            {
                Environment.ExitCode = ProbaVynos.Prognat(() => new MainForm(Sostoyanie.Zagruzit(Path.Combine(Path.GetTempPath(), "rovnyfps-proba-vynos.txt"))) { Snimok = true });
                return;
            }

            // --test-obnovlenie <номер будто бы текущей версии> <файл>: скачать выложенную версию и встать на место этого exe (проверка выкладки на копии)
            if (args.Length >= 3 && args[0] == "--test-obnovlenie")
            {
                string r;
                try
                {
                    var est = Obnovlenie.Proverit(ManifestUrl, int.Parse(args[1]));
                    if (est == null) r = "нет версии новее " + args[1];
                    else { Obnovlenie.Skachat(est); r = "OK версия " + est.Versiya + " sha256 " + est.Sha256; }
                }
                catch (Exception ex) { r = "FAIL " + ex.Message; }
                File.WriteAllText(args[2], r, new UTF8Encoding(false));
                return;
            }
            // ключи прежнего «Ровного FPS» работают как раньше: --on/--off <игра> [fps], --proverka <лог>, самопроверка ограничения — --test-limit <папка>
            if (args.Length >= 2 && (args[0] == "--on" || args[0] == "--off" || args[0] == "--proverka" || args[0] == "--test-limit"))
            { if (args[0] == "--test-limit") args[0] = "--test"; RovnyFps.Program.Main(args); return; }
            if (args.Length >= 2 && args[0] == "--test") { SelfTest(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--shot") { Shot(args[1], args.Length > 2 ? args[2] : ""); return; }
            // --katka-test <exe> <секунд> <файл>: живой замер любого процесса (без игры) — проверка цепочки PresentMon → итог
            if (args.Length >= 4 && args[0] == "--katka-test")
            {
                var g = new Game { Name = "проба", Exes = { args[1] } };
                var k = Katka.Nachat(g, itog => { });
                Thread.Sleep(int.Parse(args[2]) * 1000);
                k.Ostanovit();
                for (int i = 0; i < 100 && k.Idet; i++) Thread.Sleep(100);
                File.WriteAllText(args[3], k.Rezultat != null ? k.Rezultat.VStroku() : "нет итога (кадров мало или процесс не рисовал)", new UTF8Encoding(false));
                return;
            }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                // обслуживание Windows и замер кадров (ETW) требуют прав администратора
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args.Select(x => "\"" + x + "\""))) { Verb = "runas", UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (args.Length == 0) MessageBox.Show("Без прав администратора я не смогу ни записать настройки RivaTuner, ни замерить игру, ни поменять настройки Windows.\n\nЗапусти ещё раз и нажми «Да».",
                        "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            // Общая программа пишет те же файлы и профили RivaTuner, что прежние «Ровный FPS» и «FPS-мастер», — вместе с ними работать нельзя
            bool first, first2;
            var one = new Mutex(true, "Global\\RovnyFPS", out first);
            var two = new Mutex(true, "Global\\FpsMaster", out first2);
            if (!first || !first2)
            {
                Vstraivanie.RazreshitVperedi();
                MessageBox.Show("Уже открыта эта программа или её прежние части — «Ровный FPS» или «FPS-мастер».\n\nЗакрой их: эта программа заменяет обе.", "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.ThreadException += (s, e) => Oops(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Oops(e.ExceptionObject as Exception);
            Zhurnal.Pishi("=== Ровный FPS (общая программа) версия " + Versiya + ", " + Environment.OSVersion + ", экран " + Proverki.TekushayaHz() + " Гц, PresentMon " +
                (Zamer.PmEst ? "есть" : "нет") + ", аргументы: " + string.Join(" ", args));
            // железо узнаём в фоне вместе с первой проверкой (MainForm.Obnovit): окно открывается сразу
            Application.Run(new MainForm(Sostoyanie.Zagruzit(PutSostoyaniya)));
            GC.KeepAlive(one); GC.KeepAlive(two);
        }

        static void Oops(Exception ex)
        {
            Zhurnal.Pishi("СБОЙ: " + ex);
            MessageBox.Show("Что-то пошло не так:\n\n" + (ex != null ? ex.Message : "неизвестная ошибка") + "\n\nПодробности — в «Журнале».",
                "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Пример замера для --shot demo / --test: его Deadlock 08.11 17:30 (Замер в игре «Ровного FPS»)
        static KatkaItog Primer()
        {
            return new KatkaItog { Igra = "Deadlock", Kogda = new DateTime(2026, 11, 8, 17, 30, 0), SekundVIgre = 139, Sredniy = 96.8, Low1 = 46.4, Ryvki = 68,
                Derzhit = 73.4, CherezProc = 0, Podvisaniy = 32, Prichiny = "31 чтение с диска|1 заминка в игре или драйвере", Vperedi = "explorer 12|steamwebhelper 10|Discord 2",
                MinSvobodnoMb = 4554, VsegoMb = 16300, ExePut = @"D:\SteamLibrary\steamapps\common\Deadlock\game\bin\win64\deadlock.exe" };
        }

        // --test <файл>: проверки и подсчёты без окна и без изменений
        static void SelfTest(string put)
        {
            var sb = new StringBuilder();
            Action<bool, string> ok = (b, s) => sb.AppendLine((b ? "OK   " : "FAIL ") + s);
            Zhelezo.Uznat();
            sb.AppendLine("железо: " + Zhelezo.Cpu + " | " + Zhelezo.Gpu + " | драйвер " + Zhelezo.Drayver + " | " + Zhelezo.OzuGb + " ГБ | " + Zhelezo.Windows);
            var dl = new Game { Name = "Deadlock", Exes = { "deadlock.exe" } };
            var pr = Proverki.Sobrat(new Dictionary<string, string>(), Primer(), dl);
            foreach (var q in pr) sb.AppendLine("  " + q.Id + ": " + q.Sost + " — " + q.Nazvanie + " | " + q.Korotko + (q.Mozhno ? " | можно исправить: " + q.Knopka : ""));
            ok(pr.Count >= 18, "проверок не меньше 18 (" + pr.Count + ")");
            // общее окно: названия, скрытые пункты, факты без процентов, сетка в двух размерах
            var imena = MainForm.Nazvat(new List<Proverka> { new Proverka { Id = "vbs", Sost = Vid.Risk, Nazvanie = "x" }, new Proverka { Id = "dvr", Sost = Vid.Problema, Nazvanie = "x" },
                new Proverka { Id = "pitanie:deadlock.exe", Sost = Vid.Problema, Nazvanie = "x" }, new Proverka { Id = "dvr", Sost = Vid.Ok, Nazvanie = "как было" } }, dl);
            ok(imena[0].Nazvanie == "Изоляция ядра: целостность памяти" && imena[1].Nazvanie == "Xbox Game Bar: фоновая запись игры" && imena[2].Nazvanie == "Режим управления электропитанием" && imena[3].Nazvanie == "как было",
                "названия — как в Windows и NVIDIA, у пунктов «в порядке» не меняются");
            ok(new[] { "msi", "edge", "hdcp", "cs2filtr", "limit", "store", "tv:netthrot", "fastsync:a.exe", "ansel:a.exe" }.All(x => MainForm.Skryt(new Proverka { Id = x })) &&
               new[] { "vbs", "dvr", "chastota", "rebar", "tv:prio", "pitanie:a.exe" }.All(x => !MainForm.Skryt(new Proverka { Id = x })), "убранные пункты скрыты, остальные видны");
            // закрытие окна посреди работы ограничения: вложенное окно умеет отказать (иначе Steam останется закрытым)
            ok(typeof(RovnyFps.MainForm).GetMethod("MozhnoZakryt") != null && typeof(RovnyFps.MainForm).GetMethod("Vklyuchena") != null, "вложенное окно ограничения отвечает на закрытие и знает включённые игры");
            // обновления: манифест новее — предложить, такой же — молчать; адрес — не открытый репозиторий версии 10
            try
            {
                var mf = Path.Combine(Path.GetTempPath(), "rovnyfps-manifest.txt");
                string hvost = "\nhttps://example.invalid/x.exe\n" + new string('a', 64) + "\nчто нового\n";
                File.WriteAllText(mf, (Versiya + 1) + hvost);
                var e1 = Obnovlenie.Proverit(new Uri(mf).AbsoluteUri, Versiya);
                File.WriteAllText(mf, Versiya + hvost);
                var e2 = Obnovlenie.Proverit(new Uri(mf).AbsoluteUri, Versiya);
                File.Delete(mf);
                ok(e1 != null && e1.Versiya == Versiya + 1 && e2 == null, "обновление: версия новее предлагается, та же — нет");
            }
            catch (Exception ex) { ok(false, "обновление: " + ex.Message); }
            ok(ManifestUrl == "https://raw.githubusercontent.com/mrraketa/rovny-fps/main/versiya.txt" && ManifestUrl == RovnyFps.Program.ManifestUrl, "адрес обновлений — открытый mrraketa/rovny-fps, тот же, что у версии 10");
            MainForm.Nazvat(pr, dl);
            var sProc = pr.Where(q => q.Nado && !MainForm.Skryt(q)).Where(q => MainForm.Fakt(q).Contains("%") && q.Id != "vyvod" && q.Id != "upor").Select(q => q.Id + ": " + MainForm.Fakt(q)).ToList();
            ok(sProc.Count == 0, "в строках списков нет процентов-обещаний" + (sProc.Count > 0 ? ": " + string.Join(" | ", sProc) : ""));
            try
            {
                var tmpS = Path.Combine(Path.GetTempPath(), "rovnyfps-setka.txt"); if (File.Exists(tmpS)) File.Delete(tmpS);
                var fS = new MainForm(Sostoyanie.Zagruzit(tmpS)) { Snimok = true, Demo = Primer() };
                fS.StartPosition = FormStartPosition.Manual; fS.Location = new Point(-6000, -6000); fS.ShowInTaskbar = false;
                fS.Show(); Application.DoEvents();
                ok(!fS.KnopkaObnovleniya.Visible, "кнопки «Обновить» нет, пока не вышла версия новее");
                fS.PokazatObnovlenie(new Obnovlenie.Est { Versiya = Versiya + 1 }); Application.DoEvents();
                var ko = fS.KnopkaObnovleniya.Bounds;
                ok(fS.KnopkaObnovleniya.Visible && ko.Top >= 0 && ko.Bottom <= Ui.S(64) && ko.Right <= fS.ClientSize.Width - Ui.S(8) && ko.Left > fS.ClientSize.Width / 2,
                   "вышла версия новее — кнопка «Обновить» в шапке справа");
                foreach (var rz in new[] { new Size(1180, 760), new Size(960, 620), new Size(1600, 900) })
                {
                    fS.ClientSize = new Size(Ui.S(rz.Width), Ui.S(rz.Height)); Application.DoEvents();
                    string osh = fS.Setka();
                    ok(osh == "", "сетка окна " + rz.Width + "×" + rz.Height + (osh == "" ? ": колонки и кнопки на местах" : ": " + osh));
                    if (rz.Width == 960) { fS.RaskrytRuchnoe(true); Application.DoEvents(); osh = fS.Setka(); ok(osh == "" && fS.VidnoRuchnoe, "узкое окно: «Сделать вручную» раскрывается" + (osh == "" ? "" : ": " + osh)); fS.RaskrytRuchnoe(false); }
                }
                fS.Close(); try { File.Delete(tmpS); } catch { }
            }
            catch (Exception ex) { ok(false, "сетка окна: " + ex); }
            var cs = new Game { Name = "CS2", Exes = { "cs2.exe" } };
            sb.AppendLine("CS2:");
            foreach (var q in Proverki.Sobrat(new Dictionary<string, string>(), null, cs).Where(x => x.Id.Contains("cs2") || x.Id == "limit"))
                sb.AppendLine("  " + q.Id + ": " + q.Sost + " — " + q.Nazvanie + " | " + q.Korotko);
            var bez = Proverki.Sobrat(new Dictionary<string, string>(), null, dl);
            ok(bez.Count(q => q.Sost == Vid.NetDannyh) == 4, "без замера 4 проверки ждут катку");
            try { string r = Nvidia.ProbaBezZapisi(); ok(r.Contains("найден по exe: True") && r.Contains("после: 1") && r.Contains("снято: умолч.") && r.Contains("fastsync: True") && r.Contains("ansel: 0") && (r.Contains("старый лимит глобально: 60→снято") || r.Contains("старый лимит глобально: ошибка: NVIDIA: SetSetting — код -160")), "профиль NVIDIA в памяти, без записи: " + r); }
            catch (Exception ex) { ok(false, "профиль NVIDIA: " + ex.Message); }
            // волна 5: разбор вывода утилит и строк совместимости (без записи)
            ok(Proverki.BcdRazobrat("identifier {current}\r\nuseplatformclock        Yes\r\ndisabledynamictick      No\r\nuseplatformtick Да\r\n").SequenceEqual(new[] { "useplatformclock", "useplatformtick" }), "bcdedit разбирается");
            var idl = Proverki.IdleRazobrat("Минимальная 0x00000000\r\n Текущий индекс от сети: 0x00000001\r\n от батарей: 0x00000000");
            ok(idl != null && idl[0] == 1 && idl[1] == 0, "powercfg разбирается");
            ok(Proverki.UbratMetku("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE") == "~ HIGHDPIAWARE" && Proverki.UbratMetku("~ DISABLEDXMAXIMIZEDWINDOWEDMODE") == null, "метка совместимости убирается");
            ok(pr.Any(q => q.Id.StartsWith("tv") ), "следы твикеров проверены");
            var k = Primer(); var k2 = KatkaItog.IzStroki(k.VStroku().Split('\t'));
            ok(k2 != null && Math.Abs(k2.Low1 - 46.4) < 0.01 && k2.Prichiny == k.Prichiny && k2.ExePut == k.ExePut, "катка пишется и читается");
            ok(k.PoPrichinam().Count == 2 && k.PoPrichinam()[0].Key == 31, "причины подвисаний разбираются");
            ok(k.KtoVperedi().Count == 3 && k.KtoVperedi()[1].Value == 10, "кто был впереди разбирается");
            // было → стало
            var tmp = Path.Combine(Path.GetTempPath(), "fpsmaster-test.txt");
            if (File.Exists(tmp)) File.Delete(tmp);
            var s1 = Sostoyanie.Zagruzit(tmp);
            var a = Primer(); var b2 = Primer(); b2.Kogda = a.Kogda.AddHours(2); b2.Low1 = 55;
            s1.Katki.Add(a); s1.Katki.Add(b2); s1.Fix["dvr"] = "1;-"; s1.FixKogda["dvr"] = a.Kogda.AddHours(1); s1.Skip.Add("obsl"); s1.Spiskom = true; s1.Sohranit();
            var s2 = Sostoyanie.Zagruzit(tmp); KatkaItog by, sta; s2.ByloStalo("Deadlock", out by, out sta);
            ok(by != null && sta != null && by.Low1 < sta.Low1, "было → стало по исправлению");
            ok(s2.Fix["dvr"] == "1;-" && s2.Skip.Contains("obsl") && s2.Spiskom, "состояние сохраняется");
            File.Delete(tmp);
            // точки отката: сохраняются, читаются, план отката верный
            var s3 = Sostoyanie.Zagruzit(tmp); var t0 = new DateTime(2026, 11, 10, 12, 0, 0);
            s3.Fix["a"] = "1"; s3.FixKogda["a"] = t0;
            bool t1 = s3.SohranitTochku("первая"), t2 = s3.SohranitTochku("дубль");
            s3.Fix["b"] = "-"; s3.FixKogda["b"] = t0.AddMinutes(1); s3.Fix["c"] = "0"; s3.FixKogda["c"] = t0.AddMinutes(2);
            s3.SohranitTochku("вторая"); s3.Fix.Remove("a"); s3.FixKogda.Remove("a"); s3.Imena["b"] = "Запись Xbox"; s3.Sohranit();
            var s4 = Sostoyanie.Zagruzit(tmp);
            ok(t1 && !t2 && s4.Tochki.Count == 2 && s4.Tochki[1].Metka == "вторая" && s4.Imya("b") == "Запись Xbox", "точки отката сохраняются и читаются");
            ok(s4.ChtoVernut(s4.Tochki[0]).SequenceEqual(new[] { "c", "b" }) && s4.ChtoVklyuchit(s4.Tochki[0]).SequenceEqual(new[] { "a" }) &&
               s4.ChtoVernut(null).Count == 2 && s4.ChtoVklyuchit(s4.Tochki[1]).SequenceEqual(new[] { "a" }) && s4.ChtoVernut(s4.Tochki[1]).Count == 0, "план отката к точке верный");
            File.Delete(tmp);
            ok(TochkaWindows.MetodEst(), "точка восстановления Windows: метод найден (без создания)");
            try { using (var bmp = Otchet.Narisovat("Deadlock", Primer(), pr)) bmp.Save(Path.ChangeExtension(put, ".otchet.png")); ok(true, "отчёт рисуется"); }
            catch (Exception ex) { ok(false, "отчёт: " + ex.Message); }
            try
            {
                var it = Katka.ProbaItoga();
                ok(it != null && Math.Abs(it.SekundVIgre - 123) < 1 && it.Podvisaniy == 60 && it.UporGpu >= 85 && it.CherezProc == 0 && it.Vperedi.Contains("explorer"),
                    "итог катки из кадров: " + (it == null ? "нет" : "в бою " + Math.Round(it.SekundVIgre) + " с, средний " + Itog.Chislo(it.Sredniy) + ", 1% low " + Itog.Chislo(it.Low1) +
                    ", подвисаний " + it.Podvisaniy + ", видеокарта " + it.UporGpu + "%, впереди " + it.Vperedi));
            }
            catch (Exception ex) { ok(false, "итог катки: " + ex); }
            File.WriteAllText(put, sb.ToString(), new UTF8Encoding(true));
        }

        // PrintWindow, а не DrawToBitmap: тот рисует вложенные элементы в обратном порядке слоёв
        static void Snyat(Form f, string png)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(f.Handle, dc, 2); g.ReleaseHdc(dc); }
                bmp.Save(png);
            }
        }

        // --shot <png> [demo|spisok|demo-spisok] — снять окно (с примером замера — demo)
        static void Shot(string png, string rezhim)
        {
            bool fon = rezhim.Contains("fon");   // как в жизни: окно сразу, железо и проверки — в фоне
            if (!fon) Zhelezo.Uznat();
            var tmp = Path.Combine(Path.GetTempPath(), "fpsmaster-shot.txt");
            if (File.Exists(tmp)) File.Delete(tmp);
            var s = Sostoyanie.Zagruzit(tmp); s.Spiskom = rezhim.Contains("spisok");
            var f = new MainForm(s) { Snimok = true, Fon = fon };
            var rm = System.Text.RegularExpressions.Regex.Match(rezhim, @"razmer=(\d+)x(\d+)");   // razmer=960x620 — снять окно в этом размере
            if (rm.Success) f.ClientSize = new Size(Ui.S(int.Parse(rm.Groups[1].Value)), Ui.S(int.Parse(rm.Groups[2].Value)));
            if (rezhim.Contains("ruchnoe")) f.RaskrytRuchnoe(true);   // узкое окно с раскрытой третьей колонкой
            var om = System.Text.RegularExpressions.Regex.Match(rezhim, @"obnovlenie=(\d+)");   // obnovlenie=12 — как выглядит «вышла версия 12»
            if (om.Success) f.PokazatObnovlenie(new Obnovlenie.Est { Versiya = int.Parse(om.Groups[1].Value) });
            int ti = rezhim.IndexOf("tema=");
            if (ti >= 0) { f.Show(); Application.DoEvents(); foreach (var id in rezhim.Substring(ti + 5).Split(' ', ',')[0].Split('>')) { f.Pokrasit(Ui.ProbnayaTema(id)); Application.DoEvents(); } }   // «a>b» — смена на лету
            if (rezhim.Contains("tochki"))   // окно «Точки отката» с примером
            {
                var d0 = DateTime.Now.AddHours(-3);
                s.Fix["dvr"] = "1;-"; s.FixKogda["dvr"] = d0; s.Imena["dvr"] = "Запись Xbox в фоне";
                s.Tochki.Add(new Sostoyanie.Tochka { Kogda = d0.AddMinutes(-1), Metka = "перед: Запись Xbox в фоне" });
                s.Tochki.Add(new Sostoyanie.Tochka { Kogda = d0.AddMinutes(20), Metka = "перед «Исправить всё» (3 пункта)", Ids = { "dvr" } });
                foreach (var id in new[] { "store", "edge", "obsl" }) { s.Fix[id] = "-"; s.FixKogda[id] = d0.AddMinutes(21); }
                s.Tochki.Add(new Sostoyanie.Tochka { Kogda = d0.AddMinutes(50), Metka = "сохранена вручную", Ids = { "dvr", "edge", "obsl", "store" } });
                using (var d = new OknoTochek(s)) { d.StartPosition = FormStartPosition.Manual; d.Location = new Point(-5000, -5000); d.Show(); Application.DoEvents(); Snyat(d, png); }
                f.Dispose(); try { File.Delete(tmp); } catch { }
                return;
            }
            var chasy = Stopwatch.StartNew();
            if (rezhim.Contains("demo")) f.Demo = Primer();
            bool ekran = rezhim.Contains("ekran");
            f.StartPosition = FormStartPosition.Manual; f.Location = ekran ? new Point(60, 60) : new Point(-5000, -5000); f.ShowInTaskbar = false;
            if (ekran) f.TopMost = true;
            f.Show(); Application.DoEvents();
            if (fon)
            {
                long okno = chasy.ElapsedMilliseconds;
                Snyat(f, Path.ChangeExtension(png, ".rano.png"));   // что видно в первую секунду
                long dolgo = 0;   // самая длинная пауза главного потока, пока идут проверки
                while (f.Proveryaet && chasy.ElapsedMilliseconds < 30000)
                {
                    long t0 = chasy.ElapsedMilliseconds; Application.DoEvents(); dolgo = Math.Max(dolgo, chasy.ElapsedMilliseconds - t0); Thread.Sleep(10);
                }
                File.WriteAllText(Path.ChangeExtension(png, ".txt"), "окно показано через " + okno + " мс; проверки готовы через " + chasy.ElapsedMilliseconds +
                    " мс; самая длинная пауза окна " + dolgo + " мс");
            }
            else { f.Obnovit(); Application.DoEvents(); }
            if (ekran)
            {
                var ch2 = Stopwatch.StartNew(); while (ch2.ElapsedMilliseconds < 600) { Application.DoEvents(); Thread.Sleep(10); }
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
                    bmp.Save(png);
                }
            }
            else Snyat(f, png);
            f.Close();
            try { File.Delete(tmp); } catch { }
        }
    }
}
