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
    // Действия общего окна — взяты из FPS-мастера без изменений: применить, вернуть, точки отката, замер матча, отчёт.
    partial class MainForm
    {
        // окно с пояснением: почему, что сейчас и что станет; кнопка — действие
        void Podrobno(Proverka p)
        {
            if (p.Sdelano)
            {
                DateTime kogda;
                string kg = st.FixKogda.TryGetValue(p.Id, out kogda) ? " " + kogda.ToString("dd.MM в HH:mm") : "";
                if (Okno.Pokazat(this, p.Nazvanie, "Исправлено программой" + kg + ". Прежнее значение сохранено — можно вернуть как было.", "Вернуть как было", "Закрыть")) Vernut(p);
                return;
            }
            string tekst = p.Pochemu;
            if (p.Seychas != "" || p.Stanet != "") tekst += (tekst != "" ? "\n\n" : "") + "Сейчас: " + p.Seychas + "\nСтанет: " + p.Stanet;
            if (p.Sost == Vid.Risk && p.Cena != null) tekst += "\n\nЦена: " + p.Cena;
            if (p.Mozhno) { if (Okno.Pokazat(this, p.Nazvanie, tekst, p.Knopka, "Закрыть")) Ispravit(p, true, true); }
            else if (p.Deystvie != null)
            {
                if (Okno.Pokazat(this, p.Nazvanie, tekst, p.DeystvieTekst, "Закрыть"))
                    try { p.Deystvie(); } catch (Exception ex) { Status("Не открылось: " + ex.Message, Ui.Amber); }
            }
            else Okno.Pokazat(this, p.Nazvanie, tekst, "Понятно", null);
        }

        public static string Punktov(int n) { int d = n % 10, s = n % 100; return n + (d == 1 && s != 11 ? " пункт" : d >= 2 && d <= 4 && (s < 12 || s > 14) ? " пункта" : " пунктов"); }

        // ---------- действия ----------
        // bezTochki — точку отката уже сохранил вызывающий (пачка исправлений, сам откат)
        bool Ispravit(Proverka p, bool obnovit = true, bool bezVoprosa = false, bool bezTochki = false)
        {
            if (p.Sost == Vid.Risk && !bezVoprosa && !Okno.Pokazat(this, p.Nazvanie + ": точно?", p.Pochemu + "\n\nЦена: " + p.Cena, p.Knopka, "Не надо")) return false;
            bool vyshlo = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!bezTochki) st.SohranitTochku("перед: " + p.Nazvanie);
                string old = p.Ispravit();
                st.Fix[p.Id] = old; st.FixKogda[p.Id] = DateTime.Now; st.Skip.Remove(p.Id); st.Imena[p.Id] = p.Nazvanie; st.Sohranit();
                Zhurnal.Pishi("исправлено: " + p.Id + " (было " + old + ")");
                Status("Сделано: " + p.Nazvanie.ToLowerInvariant() + ". Сыграй матч — покажу «было → стало».", Ui.Green);
                p.Sdelano = true; p.Sost = Vid.Ok;   // на экране — сразу; фоновая проверка потом подтвердит
                if (vybran == p.Id) vybran = null;
                vyshlo = true;
            }
            catch (Exception ex) { Zhurnal.Pishi("исправить " + p.Id + " не вышло: " + ex); Status("Не вышло: " + ex.Message, Ui.Red); }
            finally { Cursor = Cursors.Default; }
            if (obnovit) Obnovit();
            return vyshlo;
        }

        // вернуть одно исправление по его id; oshibka — что пошло не так
        bool VernutId(string id, out string oshibka)
        {
            oshibka = null;
            try
            {
                string old;
                if (st.Fix.TryGetValue(id, out old))
                {
                    Action<string> v = Proverki.Vozvrat(id);
                    if (v != null) v(old);
                }
                st.Fix.Remove(id); st.FixKogda.Remove(id); st.Sohranit();
                Zhurnal.Pishi("возвращено: " + id);
                return true;
            }
            catch (Exception ex) { Zhurnal.Pishi("вернуть " + id + " не вышло: " + ex); oshibka = ex.Message; return false; }
        }

        void Vernut(Proverka p)
        {
            Cursor = Cursors.WaitCursor;
            string oshibka;
            if (VernutId(p.Id, out oshibka)) { p.Sdelano = false; Status("Вернул как было: " + p.Nazvanie.ToLowerInvariant() + ".", Ui.Muted); }
            else Status("Не вышло вернуть: " + oshibka, Ui.Red);
            Cursor = Cursors.Default;
            Obnovit();
        }

        // ---------- точки отката ----------
        void PokazatTochki()
        {
            using (var d = new OknoTochek(st))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (d.Sohranit)
                    Status(st.SohranitTochku("сохранена вручную") ? "Точка отката сохранена." : "Такое состояние уже сохранено — новая точка не нужна.", Ui.Muted);
                else if (d.Otkat) OtkatitK(d.Vybrana);
                else if (d.Windows) SozdatTochkuWindows();
            }
        }

        // Точка восстановления Windows — только по его кнопке и с вопросом; идёт в фоне (до минуты)
        void SozdatTochkuWindows()
        {
            if (!Okno.Pokazat(this, "Создать точку восстановления Windows?",
                "Это средство самой Windows, а не программы: снимок системных файлов и реестра. Создаётся до минуты и занимает место на диске C:.\n\n" +
                "Откат к ней делает Windows («Восстановление системы») и затрагивает всё, что менялось после точки, — не только настройки из этой программы.\n\n" +
                "Программе она не нужна: её собственные точки откатывают её изменения сами.", "Создать", "Не надо")) return;
            string opis = "Ровный FPS " + DateTime.Now.ToString("dd.MM HH:mm");
            Status("Создаю точку восстановления Windows — это до минуты…", Ui.Muted);
            Zhurnal.Pishi("точка восстановления Windows: создаю «" + opis + "»");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string osh = TochkaWindows.Sozdat(opis);
                Zhurnal.Pishi(osh == null ? "точка восстановления Windows создана" : "точка восстановления Windows не создана: " + osh);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (osh == null) Status("Точка восстановления Windows создана: «" + opis + "». Откат к ней — через «Восстановление системы» Windows.", Ui.Green);
                        else Status("Точка восстановления Windows не создана: " + osh + ".", Ui.Amber);
                    });
                }
                catch (Exception) { }
            });
        }

        static string Perechen(IEnumerable<string> imena)
        {
            var l = imena.ToList();
            return string.Join("\n", l.Take(8).Select(x => "• " + x)) + (l.Count > 8 ? "\n… и ещё " + (l.Count - 8) : "");
        }

        // t == null — «до программы»: вернуть всё
        void OtkatitK(Sostoyanie.Tochka t)
        {
            var vernut = st.ChtoVernut(t); var vkl = st.ChtoVklyuchit(t);
            if (vernut.Count == 0 && vkl.Count == 0) return;
            string kuda = t == null ? "до программы" : "к точке " + t.Kogda.ToString("dd.MM HH:mm");
            string tekst = (vernut.Count > 0 ? "Верну как было:\n" + Perechen(vernut.Select(st.Imya)) : "") + (vernut.Count > 0 && vkl.Count > 0 ? "\n\n" : "") +
                (vkl.Count > 0 ? "Включу снова:\n" + Perechen(vkl.Select(st.Imya)) : "");
            if (!Okno.Pokazat(this, "Откатить " + kuda + "?", tekst, "Откатить", "Не надо")) return;
            st.SohranitTochku("перед откатом " + kuda);   // откат тоже можно отменить
            Zhurnal.Pishi("откат " + kuda + ": вернуть " + vernut.Count + ", включить " + vkl.Count);
            int ok = 0, ne = 0; string oshibka;
            Cursor = Cursors.WaitCursor;
            foreach (var id in vernut) { if (VernutId(id, out oshibka)) ok++; else ne++; }
            foreach (var id in vkl)
            {
                var p = proverki.FirstOrDefault(q => q.Id == id && q.Mozhno && !q.Sdelano);
                if (p != null && Ispravit(p, false, true, true)) ok++; else { ne++; Zhurnal.Pishi("откат: включить снова " + id + " не вышло"); }
            }
            Cursor = Cursors.Default;
            if (ne == 0) Status("Откатил " + kuda + ": " + Punktov(ok) + ".", Ui.Green);
            else Status("Откат " + kuda + ": получилось " + ok + ", не вышло " + ne + " — подробности в «Журнале».", Ui.Amber);
            Obnovit();
        }

        void Propustit(Proverka p) { st.Skip.Add(p.Id); if (vybran == p.Id) vybran = null; st.Sohranit(); prokrutka = 0; Perestroit(); }

        // ---------- замер ----------
        void PodgotovitPm()
        {
            if (Zamer.PmEst || Zamer.VzyatURovnogo()) return;
            if (st.PmOtkaz) { Status("Замер выключен: нет PresentMon.", Ui.Amber); return; }
            if (!Okno.Pokazat(this, "Скачать PresentMon?", "Замер кадров делает бесплатная PresentMon от Intel (около 1 МБ, с GitHub). " +
                "Я проверю её контрольную сумму. Без неё «было → стало» не посчитать.", "Скачать", "Не сейчас"))
            { st.PmOtkaz = true; st.Sohranit(); Status("Замер выключен: нет PresentMon.", Ui.Amber); geroy.Invalidate(); return; }
            Status("Качаю PresentMon…", Ui.Muted);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string oshibka = null;
                try { Zamer.SkachatPm(); Zhurnal.Pishi("PresentMon скачан"); } catch (Exception ex) { oshibka = ex.Message; Zhurnal.Pishi("PresentMon не скачался: " + ex); }
                BeginInvoke((Action)delegate { Status(oshibka == null ? "PresentMon готов. Сыграй матч — замерю." : "PresentMon не скачался: " + oshibka, oshibka == null ? Ui.Green : Ui.Red); });
            });
        }

        // Какая игра запущена — смотрим в фоне: перебор процессов раз в 3 с в главном потоке давал микроподвисания окна
        void SleditZaIgroy()
        {
            if (katka != null && katka.Idet) return;
            if (obnovlyaemsya) return;   // программа перезапускается — замер начнёт новая копия
            if (!Zamer.PmEst || skanIdet) return;
            skanIdet = true;
            var spisok = igry.ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Game nashel = null;
                try
                {
                    var imena = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pr in Process.GetProcesses()) { try { imena.Add(pr.ProcessName); } catch (Exception) { } pr.Dispose(); }
                    nashel = spisok.FirstOrDefault(g => g.Exes.Any(e => imena.Contains(Path.GetFileNameWithoutExtension(e))));
                }
                catch (Exception ex) { Zhurnal.Pishi("поиск игры: " + ex.Message); }
                try { BeginInvoke((Action)delegate { skanIdet = false; if (nashel != null && !IsDisposed) NachatKatku(nashel); }); }
                catch (Exception) { skanIdet = false; }
            });
        }

        void NachatKatku(Game g)
        {
            if (katka != null && katka.Idet) return;
            var gg = g;
            if (igra != g) SmenitIgru(g);
            Katka k = null;
            k = Katka.Nachat(g, itog => { try { BeginInvoke((Action)delegate { KatkaKonchilas(gg, k); }); } catch (Exception) { } });
            katka = k;
            Status("Идёт замер: " + g.Name + ". Итог — когда закроешь игру.", Ui.Green);
        }

        void KatkaKonchilas(Game g, Katka k)
        {
            if (k == null || k.Rezultat == null) { Status("Матч " + g.Name + " вышел слишком коротким для замера.", Ui.Muted); return; }
            st.Katki.Add(k.Rezultat); st.Sohranit();
            Status("Матч " + g.Name + " замерен: средний FPS " + Itog.Chislo(k.Rezultat.Sredniy) + ", 1% low " + Itog.Chislo(k.Rezultat.Low1) + ".", Ui.Green);
            if (igra == g) Obnovit();
        }

        // ---------- отчёт ----------
        void SdelatOtchet()
        {
            try
            {
                string put = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ровный FPS отчёт " + DateTime.Now.ToString("dd.MM HH-mm") + ".png");
                using (var bmp = Otchet.Narisovat(igra.Name, Demo ?? st.Poslednyaya(igra.Name), proverki)) bmp.Save(put, System.Drawing.Imaging.ImageFormat.Png);
                Zhurnal.Pishi("отчёт сохранён: " + put);
                Status("Отчёт сохранён на рабочий стол.", Ui.Green);
                try { Process.Start("explorer.exe", "/select,\"" + put + "\""); } catch { }
            }
            catch (Exception ex) { Zhurnal.Pishi("отчёт не сохранился: " + ex); Status("Отчёт не сохранился: " + ex.Message, Ui.Red); }
        }
    }
}
