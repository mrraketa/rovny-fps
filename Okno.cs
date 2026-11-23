// Общее окно «Ровный FPS» (с 15.11.2026): слева ограничение FPS (окно прежнего «Ровного FPS», вложенное как есть),
// посередине — настройки, которые программа меняет сама, справа — то, что делается руками.
// Макет и решения: fps-master\maket-obedinenie (okno2.html, nazvaniya.html). Правила вида:
//  - названия пунктов — как в Windows / NVIDIA / игре; над названием мелко — где настройка живёт;
//  - в списках только то, что программа проверила на этом ПК и что правда не сделано; процентов прироста нет;
//  - чего программа проверить не может — отдельный блок «Проверь сам»;
//  - перестройка списков — заменой (новое поверх старого), WM_SETREDRAW нельзя: ломает вкладку оболочки.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FpsMaster
{
    // Прокручиваемый список одной колонки
    class Lenta : Panel
    {
        Panel sloy; Kartochka pz; int prokr, tyanuY = -1, tyanuTop;
        public Lenta() { BackColor = Ui.Bg; }
        public int Shirina { get { return Math.Max(10, Width - Ui.S(8)); } }   // справа место под ползунок

        public void Zamenit(Panel novyy)
        {
            int niz = novyy.Controls.Count == 0 ? 0 : novyy.Controls.Cast<Control>().Max(c => c.Bottom);
            novyy.SetBounds(0, 0, Width, Math.Max(Height, niz + Ui.S(4)));
            var staryy = sloy; var staryyPz = pz;
            sloy = novyy; pz = null; tyanuY = -1;
            prokr = Math.Max(0, Math.Min(prokr, sloy.Height - Height));
            sloy.Top = -prokr;
            SuspendLayout();
            Controls.Add(sloy); sloy.BringToFront();
            if (sloy.Height > Height)
            {
                var p = pz = new Kartochka { Zalivka = Ui.Faint, Cursor = Cursors.Hand };
                p.MouseDown += (o, e) => { tyanuY = Cursor.Position.Y; tyanuTop = prokr; };
                p.MouseMove += (o, e) => { if (tyanuY >= 0) Postavit(tyanuTop + (Cursor.Position.Y - tyanuY) * (sloy.Height - Height) / Math.Max(1, Height - p.Height)); };
                p.MouseUp += delegate { tyanuY = -1; };
                Controls.Add(p); p.BringToFront(); Podvinut();
            }
            if (staryy != null) staryy.Dispose();
            if (staryyPz != null) staryyPz.Dispose();
            ResumeLayout();
        }

        public void Prokrutit(int na) { Postavit(prokr + na); }

        void Postavit(int v)
        {
            if (sloy == null || sloy.IsDisposed || sloy.Height <= Height) return;
            prokr = Math.Max(0, Math.Min(v, sloy.Height - Height));
            sloy.Top = -prokr; Podvinut(); Update();
        }

        void Podvinut()
        {
            if (pz == null) return;
            int h = Math.Max(Ui.S(28), Height * Height / sloy.Height);
            int y = (int)((long)prokr * (Height - h) / Math.Max(1, sloy.Height - Height));
            pz.SetBounds(Width - Ui.S(5), y, Ui.S(5), h);
        }
    }

    partial class MainForm : Form, IMessageFilter
    {
        readonly Sostoyanie st;
        static readonly object zamok = new object();   // проверки ПК — по одной за раз
        int pokolenie;       // номер последнего запроса проверок: ответы на старые запросы выбрасываем
        bool proveryayu, skanIdet;
        string vybran; int prokrutka;   // нужны общим действиям (Deystviya.cs)
        List<Game> igry;
        Game igra;
        List<Proverka> proverki = new List<Proverka>();
        readonly Kartochka geroy = new Kartochka();     // полоса «что сделать сейчас» + замер
        readonly Lenta lenta2 = new Lenta(), lenta3 = new Lenta();
        readonly Btn vseB = new Btn(Btn.Kind.Primary, "Применить все без риска"), otkatB = new Btn(Btn.Kind.Secondary, "Откатить");
        readonly Kartochka rel = new Kartochka();       // узкое окно: свёрнутая колонка, щелчок раскрывает
        readonly Label status = Ui.Tekst("", Ui.Small, Ui.Muted), sdelal = Ui.Tekst("", Ui.Small, Ui.Muted);
        readonly LinkLabel sdelalPokazat = Ui.Ssylka("Показать");
        readonly LinkLabel tochki = Ui.Ssylka("Откат изменений"), otchet = Ui.Ssylka("Сохранить отчёт"), zhurnal = Ui.Ssylka("Журнал"), podderzhat = Ui.Ssylka("Поддержать автора");
        readonly System.Windows.Forms.Timer sledit = new System.Windows.Forms.Timer();
        readonly ToolTip podskazka = new ToolTip();
        readonly RovnyFps.Core rovnyCore;
        readonly RovnyFps.MainForm rovny;             // левая колонка: ограничение FPS
        Katka katka;
        Image logo;
        Color statusCvet = Ui.Muted;
        readonly Btn upd = new Btn(Btn.Kind.Chip, "Обновить") { Selected = true, Visible = false };   // видна, только когда вышла версия новее
        Obnovlenie.Est novoe;
        bool obnovlyaemsya;   // идёт скачивание новой версии: ограничение FPS и замер не начинать — программа вот-вот перезапустится
        public Control KnopkaObnovleniya { get { return upd; } }
        Rectangle k1, k2, k3;                           // рамки колонок
        bool uzko, ruchnoeOtkryto;
        string zag2 = "", zag3 = "";
        int relN2, relN3;
        static readonly Font Nad = new Font("Segoe UI Semibold", 7.5f);
        public KatkaItog Demo;   // --shot demo: показать замер, не записывая
        public bool Snimok;      // --shot: без вопросов и без замера
        public bool Fon;         // --shot …fon: проверки в фоне, как в обычном окне
        public bool Proveryaet { get { return proveryayu; } }

        static int S(float v) { return Ui.S(v); }

        public MainForm(Sostoyanie s)
        {
            st = s;
            using (var gr = CreateGraphics()) Ui.K = gr.DpiX / 96f;
            Text = "Ровный FPS"; Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Txt;
            FormBorderStyle = FormBorderStyle.Sizable; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(760)); MinimumSize = SizeFromClientSize(new Size(S(960), S(620)));
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { var r = typeof(MainForm).Assembly.GetManifestResourceStream("logo64.png"); if (r != null) logo = Image.FromStream(r); } catch { }

            geroy.Risovat = RisovatPolosu;
            rovnyCore = new RovnyFps.Core();
            rovny = new RovnyFps.MainForm(rovnyCore, true);
            rovny.Izmenilos += delegate { if (!IsDisposed) { SinhrIgru(); geroy.Invalidate(); } };

            vseB.Click += delegate { PrimenitPachku(Bezopasnye(), "Применить " + Punktov(Bezopasnye().Count) + "?", "перед «Применить все»"); };
            otkatB.Click += delegate { PokazatTochki(); };
            rel.Zalivka = Ui.Bg; rel.BackColor = Ui.Bg; rel.Cursor = Cursors.Hand; rel.Risovat = RisovatRel;
            rel.Click += delegate { ruchnoeOtkryto = !ruchnoeOtkryto; Razlozhit(); };
            rel.AccessibleName = "Показать другую колонку";
            tochki.LinkClicked += delegate { PokazatTochki(); };
            otchet.LinkClicked += delegate { SdelatOtchet(); };
            zhurnal.LinkClicked += delegate { PokazatZhurnal(); };
            podderzhat.LinkClicked += delegate { try { Process.Start(RovnyFps.Program.SupportUrl); } catch { } };
            sdelalPokazat.LinkClicked += delegate { PokazatSdelannoeRukami(); };
            status.TextAlign = ContentAlignment.TopRight; status.AutoEllipsis = true;
            upd.Click += delegate { ObnovitProgrammu(); };
            Controls.AddRange(new Control[] { geroy, rovny, lenta2, lenta3, vseB, otkatB, rel, sdelal, sdelalPokazat, tochki, otchet, zhurnal, podderzhat, status, upd });
            rovny.Show();

            igry = st.Igry();
            igra = igry.FirstOrDefault(g => g.Name == st.Igra) ?? igry[0];
            sledit.Interval = 3000; sledit.Tick += delegate { SleditZaIgroy(); };
            Vstraivanie.TemaSmenilas += () => Pokrasit(Vstraivanie.Tema);
            Razlozhit();
        }

        // Тема оболочки пришла или сменилась (null — оболочка отпустила окно): перекраска на лету, без перезапуска
        public void Pokrasit(TemaObolochki t)
        {
            if (IsDisposed) return;
            var map = Ui.Primenit(t);
            Color n; if (map.TryGetValue(statusCvet.ToArgb(), out n)) statusCvet = n;
            foreach (Form f in Application.OpenForms) Ui.Perekrasit(f, map);
            rovny.Pokrasit(t);
            rel.Zalivka = rel.BackColor = lenta2.BackColor = lenta3.BackColor = Ui.Bg;
            Perestroit();
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Vstraivanie.Podklyuchit(Handle, null);   // необязательное встраивание во вкладку оболочки; значка в трее у нас нет
            Ui.TemnyyZagolovok(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Application.AddMessageFilter(this);
            if (Snimok) rovny.LoadNow();
            else
            {
                // то же, что делал при запуске прежний «Ровный FPS»: починить старые профили, проверить автозапуск RivaTuner
                try { rovnyCore.LoadState(); rovnyCore.FixOldProfiles(); } catch (Exception ex) { RovnyFps.Zhurnal.Pishi("старые профили: " + ex.Message); }
                ThreadPool.QueueUserWorkItem(delegate { try { rovnyCore.RefreshAutostart(); } catch (Exception ex) { RovnyFps.Zhurnal.Pishi("автозапуск: " + ex.Message); } });
                rovny.Reload(null);
            }
            Obnovit();
            if (!Snimok) { PodgotovitPm(); sledit.Start(); SleditZaIgroy(); ProveritObnovlenie(); }
        }

        // ---- обновление программы (Obnovlenie.cs): кнопка появляется, только когда выложена версия новее
        public void PokazatObnovlenie(Obnovlenie.Est est)
        {
            if (est == null || IsDisposed) return;
            novoe = est; upd.Visible = true; upd.BringToFront();
            podskazka.SetToolTip(upd, "Скачать версию " + est.Versiya + " и перезапустить программу");
            Invalidate(new Rectangle(0, 0, Width, S(64)));
        }

        void ProveritObnovlenie()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var est = Obnovlenie.Proverit(Program.ManifestUrl, Program.Versiya);   // нет сети или новой версии — null, окно как было
                if (est == null) return;
                Zhurnal.Pishi("обновление: вышла версия " + est.Versiya);
                try { BeginInvoke((Action)delegate { PokazatObnovlenie(est); }); } catch (Exception) { }
            });
        }

        void ObnovitProgrammu()
        {
            if (novoe == null) return;
            // посреди работы закрываться нельзя — сначала закончить
            if (rovny.VRabote) { Status("Сначала дождись, пока закончится работа с ограничением FPS.", Ui.Amber); return; }
            if (katka != null && katka.Idet) { Status("Идёт замер матча — обнови после игры.", Ui.Amber); return; }
            var est = novoe;
            upd.Enabled = false;
            obnovlyaemsya = true; rovny.Enabled = false;   // пока качается, новую работу не начать: иначе окно не закроется и останутся две копии
            Status("Скачиваю версию " + est.Versiya + "…", Ui.Muted);
            Zhurnal.Pishi("обновление: скачиваю версию " + est.Versiya);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string oshibka = null;
                try { Obnovlenie.Skachat(est); } catch (Exception ex) { oshibka = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed) return;
                        if (oshibka != null) { Zhurnal.Pishi("обновление не встало: " + oshibka); obnovlyaemsya = false; rovny.Enabled = true; upd.Enabled = true; Status(oshibka, Ui.Red); return; }
                        Zhurnal.Pishi("обновление: файл заменён, перезапуск");
                        Obnovlenie.Perezapustit();
                        Close();
                    });
                }
                catch (Exception) { }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // посреди включения ограничения закрывать нельзя: Steam останется закрытым, профиль — недописанным
            // идёт подбор по замеру — он отменяется (профиль вернётся), окно закроется следующим нажатием
            if (!rovny.MozhnoZakryt()) { e.Cancel = true; Status("Заканчиваю с ограничением FPS — закрой окно ещё раз через пару секунд.", Ui.Amber); return; }
            if (katka != null && katka.Idet) katka.Ostanovit();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { Application.RemoveMessageFilter(this); pokolenie++; base.OnFormClosed(e); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated && WindowState != FormWindowState.Minimized) Razlozhit();
        }

        // колесо мыши крутит тот список, над которым курсор
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || Application.OpenForms.Cast<Form>().Any(f => f.TopLevel && f != this && f.Visible)) return false;
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            foreach (var l in new[] { lenta2, lenta3 })
                if (l.Visible && l.IsHandleCreated && l.RectangleToScreen(l.ClientRectangle).Contains(Cursor.Position)) { l.Prokrutit(-delta * S(56) / 120); return true; }
            return false;
        }

        // ---------- расстановка: одна сетка на три колонки (заголовок · список · низ) ----------
        void Razlozhit()
        {
            if (rovny == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, P = S(16);
            if (W < S(500) || H < S(400)) return;
            uzko = W < S(1100);
            geroy.SetBounds(P, S(64), W - 2 * P, S(66));
            int uw = TextRenderer.MeasureText(upd.Text, Ui.Small).Width + S(32);
            upd.SetBounds(W - P - uw, S(16), uw, S(30));
            int top = S(140), kh = H - S(36) - top, zz = S(12);
            int w1 = uzko ? S(300) : S(330);
            k1 = new Rectangle(P, top, w1, kh);
            int ost = W - 2 * P - w1 - zz;
            if (!uzko)
            {
                int w2 = (ost - zz) / 2;
                k2 = new Rectangle(k1.Right + zz, top, w2, kh);
                k3 = new Rectangle(k2.Right + zz, top, ost - zz - w2, kh);
                rel.Visible = false;
            }
            else
            {
                // третья колонка свёрнута в полоску; щелчок меняет их местами
                int wr = S(46), wm = ost - zz - wr;
                if (!ruchnoeOtkryto) { k2 = new Rectangle(k1.Right + zz, top, wm, kh); k3 = Rectangle.Empty; rel.SetBounds(k2.Right + zz, top, wr, kh); }
                else { k2 = Rectangle.Empty; rel.SetBounds(k1.Right + zz, top, wr, kh); k3 = new Rectangle(rel.Right + zz, top, wm, kh); }
                rel.Visible = true;
            }
            int px = S(14), py = S(12), zg = S(40), fut = S(44);
            rovny.Razlozhit(k1.Width - 2 * px, kh - 2 * py - zg);
            rovny.Location = new Point(k1.X + px, top + py + zg);
            bool v2 = !k2.IsEmpty, v3 = !k3.IsEmpty;
            lenta2.Visible = vseB.Visible = otkatB.Visible = v2;
            if (v2)
            {
                lenta2.SetBounds(k2.X + px, top + py + zg, k2.Width - 2 * px, kh - 2 * py - zg - fut - S(8));
                int ow = S(110);
                vseB.SetBounds(k2.X + px, k2.Bottom - py - fut, k2.Width - 2 * px - ow - S(8), fut);
                otkatB.SetBounds(k2.Right - px - ow, k2.Bottom - py - fut, ow, fut);
            }
            lenta3.Visible = sdelal.Visible = v3;
            if (v3)
            {
                lenta3.SetBounds(k3.X + px, top + py + zg, k3.Width - 2 * px, kh - 2 * py - zg - fut - S(8));
                sdelal.SetBounds(k3.X + px, k3.Bottom - py - S(30), k3.Width - 2 * px - S(80), S(20));
                sdelalPokazat.Location = new Point(k3.Right - px - TextRenderer.MeasureText(sdelalPokazat.Text, Ui.Small).Width, k3.Bottom - py - S(30));
            }
            int y = H - S(26), x = P;
            foreach (var l in new[] { tochki, otchet, zhurnal, podderzhat }) { l.Location = new Point(x, y); x += TextRenderer.MeasureText(l.Text, Ui.Small).Width + S(16); }
            status.SetBounds(x + S(10), y, Math.Max(S(40), W - P - x - S(10)), S(20));
            Perestroit();
            Invalidate();
        }

        // ---------- шапка и рамки колонок ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int P = S(16), W = ClientSize.Width;
            if (logo != null) g.DrawImage(logo, P, S(12), S(40), S(40));
            TextRenderer.DrawText(g, "Ровный FPS", Ui.Title, new Point(P + S(50), S(8)), Ui.Txt, Ui.Bg);
            // справа вверху — номер версии; вышла новая — на его месте кнопка «Обновить», слева от неё «Вышла версия N»
            bool estNovaya = upd.Visible && novoe != null;
            string ver = estNovaya ? "Вышла версия " + novoe.Versiya : "версия " + Program.Versiya;
            int vw = TextRenderer.MeasureText(ver, Ui.Small).Width;
            int vx = estNovaya ? upd.Left - S(10) - vw : W - P - vw;
            TextRenderer.DrawText(g, ver, Ui.Small, new Point(vx, S(24)), Ui.Muted, Ui.Bg);
            if (estNovaya) vw = W - P - vx;
            string zh = !Zhelezo.Gotovo ? "Смотрю, что за компьютер…" :
                Zhelezo.Cpu + " · " + Zhelezo.Gpu + " · " + Zhelezo.OzuGb + " ГБ · монитор " + Proverki.Pokazat(Proverki.TekushayaHz()) + " Гц";
            TextRenderer.DrawText(g, zh, Ui.Small, new Rectangle(P + S(53), S(37), W - 2 * P - S(60) - vw, S(20)), Ui.Muted, Ui.Bg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            Ramka(g, k1, 1, "Ограничение FPS", "через RivaTuner");
            Ramka(g, k2, 2, "Настройки в один клик", zag2);
            Ramka(g, k3, 3, "Сделать вручную", zag3);
        }

        void Ramka(Graphics g, Rectangle k, int n, string nazv, string pravo)
        {
            if (k.IsEmpty) return;
            Ui.Fill(g, new RectangleF(k.X, k.Y, k.Width - 1, k.Height - 1), S(Ui.Rad + 3), Ui.Bg, Ui.Border);
            int x = k.X + S(14), y = k.Y + S(12);
            Znachok(g, x, y + S(1), n.ToString());
            int nw = TextRenderer.MeasureText(nazv, Ui.Name).Width, pw = TextRenderer.MeasureText(pravo, Ui.Small).Width;
            TextRenderer.DrawText(g, nazv, Ui.Name, new Rectangle(x + S(31), y, k.Width - S(60), S(24)), Ui.Txt, Ui.Bg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (pravo != "" && x + S(31) + nw + S(10) + pw <= k.Right - S(14))
                TextRenderer.DrawText(g, pravo, Ui.Small, new Rectangle(k.Right - S(14) - pw, y, pw, S(24)), Ui.Muted, Ui.Bg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var pen = new Pen(Ui.Border)) g.DrawLine(pen, x, y + S(32), k.Right - S(15), y + S(32));
        }

        // номер колонки: тёмный квадратик со скруглением
        static void Znachok(Graphics g, int x, int y, string t)
        {
            Ui.Fill(g, new RectangleF(x, y, S(22), S(22)), S(6), Ui.Txt, Color.Empty);
            TextRenderer.DrawText(g, t, Ui.SmallSemi, new Rectangle(x, y, S(22), S(22)), Ui.Bg, Ui.Txt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // свёрнутая колонка: номер, сколько в ней пунктов и название сверху вниз
        void RisovatRel(Graphics g)
        {
            int w = rel.Width, h = rel.Height;
            bool tri = !ruchnoeOtkryto;
            Ui.Fill(g, new RectangleF(0, 0, w - 1, h - 1), S(Ui.Rad + 3), Ui.Bg, Ui.Border);
            Znachok(g, (w - S(22)) / 2, S(12), tri ? "3" : "2");
            int n = tri ? relN3 : relN2;
            int y = S(44);
            if (n > 0)
            {
                Ui.Fill(g, new RectangleF((w - S(24)) / 2, y, S(24), S(22)), S(11), Ui.Akcent, Color.Empty);
                TextRenderer.DrawText(g, n.ToString(), Ui.SmallSemi, new Rectangle((w - S(24)) / 2, y, S(24), S(22)), Ui.NaAkcente, Ui.Akcent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                y += S(32);
            }
            string t = tri ? "Сделать вручную" : "Настройки в один клик";
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            var st0 = g.Save();
            g.TranslateTransform(w / 2f + Ui.Name.Height / 2f, y);
            g.RotateTransform(90);
            using (var b = new SolidBrush(Ui.Txt)) g.DrawString(t, Ui.Name, b, 0, 0);
            g.Restore(st0);
        }

        // --- для самопроверки и снимков ---
        public void RaskrytRuchnoe(bool da) { ruchnoeOtkryto = da; Razlozhit(); }
        public bool VidnoRuchnoe { get { return lenta3.Visible; } }
        // пустая строка — сетка в порядке: ничего не вылезло за окно, колонки не пересекаются, нижние кнопки на одной линии
        public string Setka()
        {
            var l = new List<string>(); var cl = ClientRectangle;
            foreach (Control c in Controls) if (c.Visible && c.Width > 0 && !cl.Contains(c.Bounds)) l.Add(c.GetType().Name + " «" + c.Text + "» за краем окна: " + c.Bounds);
            var kol = new[] { k1, k2, k3 }.Where(k => !k.IsEmpty).ToList(); if (rel.Visible) kol.Add(rel.Bounds);
            for (int i = 0; i < kol.Count; i++) for (int j = i + 1; j < kol.Count; j++) if (kol[i].IntersectsWith(kol[j])) l.Add("колонки пересекаются: " + kol[i] + " и " + kol[j]);
            if (kol.Any(k => k.Top != k1.Top || k.Bottom != k1.Bottom)) l.Add("колонки разной высоты");
            if (!uzko && Math.Abs(k2.Width - k3.Width) > 1) l.Add("колонки 2 и 3 разной ширины");
            if (vseB.Visible && (vseB.Bottom != rovny.Bottom || otkatB.Bottom != rovny.Bottom)) l.Add("нижние кнопки не на одной линии: " + rovny.Bottom + " и " + vseB.Bottom);
            if (uzko && lenta2.Visible == lenta3.Visible) l.Add("в узком окне должна быть видна одна из колонок 2 и 3");
            if (!uzko && !(lenta2.Visible && lenta3.Visible)) l.Add("в широком окне видны не все колонки");
            return string.Join("; ", l);
        }

        void Status(string t, Color c) { status.Text = t ?? ""; status.ForeColor = c == Ui.Green ? Ui.Txt : c; statusCvet = c; }

        static string Min(double sekund) { return sekund >= 90 ? Math.Round(sekund / 60) + " мин" : Math.Round(sekund) + " с"; }

        // ---------- полоса: что сделать сейчас + цифры замера ----------
        string Upor()
        {
            var p = proverki.FirstOrDefault(q => q.Id == "upor");
            if (p == null || p.Sost == Vid.NetDannyh) return "—";
            if (p.Sost != Vid.Ok && p.Nazvanie.Contains("процессор")) return "в процессор";
            if (p.Sost != Vid.Ok && p.Nazvanie.Contains("видеокарт")) return "в видеокарту";
            return "ровно";
        }

        void RisovatPolosu(Graphics g)
        {
            int w = geroy.Width, h = geroy.Height;
            KatkaItog bylo = null, stalo = null;
            if (Demo == null) st.ByloStalo(igra.Name, out bylo, out stalo);
            var posl = Demo ?? st.Poslednyaya(igra.Name);
            bool sravn = bylo != null && stalo != null;
            int pravo = w - S(18);
            const TextFormatFlags np = TextFormatFlags.NoPadding;

            // справа — замер
            if (posl == null)
            {
                int tw = Math.Max(TextRenderer.MeasureText("Появится после первого матча.", Ui.Small).Width, S(120));
                TextRenderer.DrawText(g, "Замера " + igra.Name + " ещё нет.", Ui.Small, new Rectangle(pravo - tw, S(15), tw, S(18)), Ui.Txt, Ui.Card, np | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "Появится после первого матча.", Ui.Small, new Point(pravo - tw, S(33)), Ui.Muted, Ui.Card, np);
                pravo -= tw + S(18);
            }
            else
            {
                var a = sravn ? bylo : posl;
                if (!uzko)
                {
                    string i1 = posl.Igra, i2 = sravn ? bylo.Kogda.ToString("dd.MM") + " → " + stalo.Kogda.ToString("dd.MM") : posl.Kogda.ToString("dd.MM HH:mm") + ", " + Min(posl.SekundVIgre);
                    int iw = Math.Max(TextRenderer.MeasureText(i1, Ui.Small).Width, TextRenderer.MeasureText(i2, Ui.Small).Width);
                    TextRenderer.DrawText(g, i1, Ui.Small, new Point(pravo - iw, S(15)), Ui.Txt, Ui.Card, np);
                    TextRenderer.DrawText(g, i2, Ui.Small, new Point(pravo - iw, S(33)), Ui.Muted, Ui.Card, np);
                    pravo -= iw + S(16);
                    using (var pen = new Pen(Ui.Border)) g.DrawLine(pen, pravo, S(14), pravo, h - S(14));
                    pravo -= S(16);
                }
                var cols = new[] {
                    new { P = "Упор", Z = Upor(), N = (string)null, L = true, Mal = true },
                    new { P = "Фризов в минуту", Z = Itog.Chislo(a.Ryvki), N = sravn ? Itog.Chislo(stalo.Ryvki) : null, L = sravn && stalo.Ryvki < a.Ryvki, Mal = false },
                    new { P = "1% low", Z = Itog.Chislo(a.Low1), N = sravn ? Itog.Chislo(stalo.Low1) : null, L = sravn && stalo.Low1 > a.Low1, Mal = false },
                    new { P = "Средний FPS", Z = Itog.Chislo(a.Sredniy), N = sravn ? Itog.Chislo(stalo.Sredniy) : null, L = sravn && stalo.Sredniy > a.Sredniy, Mal = false } };
                foreach (var c in cols)
                {
                    var f = c.Mal ? Ui.Name : Ui.Head;
                    string str = c.N != null ? " → " + c.N : "";
                    int zw = TextRenderer.MeasureText(c.Z, f, Size.Empty, np).Width, sw = str == "" ? 0 : TextRenderer.MeasureText(str, f, Size.Empty, np).Width;
                    int cw = Math.Max(TextRenderer.MeasureText(c.P, Ui.Small, Size.Empty, np).Width, zw + sw);
                    int x = pravo - cw;
                    TextRenderer.DrawText(g, c.P, Ui.Small, new Point(x, S(13)), Ui.Muted, Ui.Card, np);
                    TextRenderer.DrawText(g, c.Z, f, new Point(x, c.Mal ? S(33) : S(29)), Ui.Txt, Ui.Card, np);
                    if (str != "") TextRenderer.DrawText(g, str, f, new Point(x + zw, S(29)), c.L ? Ui.Green : Ui.Amber, Ui.Card, np);
                    pravo = x - S(24);
                }
                pravo += S(6);
            }

            // слева — четыре шага; текущий подсвечен
            int bezN = Bezopasnye().Count;
            var got = new[] { rovny.EstVklyuchennye, posl != null, bezN == 0 && proverki.Count > 0, sravn };
            int tek = Array.IndexOf(got, false);
            string[] dl = { "Выбери игру и включи ограничение FPS", "Сыграй матч — FPS замерю сам", bezN > 0 ? "Примени настройки — их " + bezN : "Примени настройки", "Сыграй ещё — покажу разницу" };
            string[] kor = { "Игра", "Замер", "Настройки", "Разница" };
            int mesto = pravo - S(18);
            // подписи сокращаются, пока не влезут: 0 — как есть, 1 — будущие коротко, 2 — подпись только у текущего
            for (int rezhim = 0; rezhim < 3; rezhim++)
            {
                var teksty = new string[4]; int vsego = 0;
                for (int i = 0; i < 4; i++)
                {
                    teksty[i] = i == tek ? dl[i] : got[i] ? (rezhim < 2 ? kor[i] : "") : rezhim == 0 ? dl[i] : rezhim == 1 ? kor[i] : "";
                    vsego += S(22) + (teksty[i] == "" ? 0 : S(7) + TextRenderer.MeasureText(teksty[i], i == tek ? Ui.SmallSemi : Ui.Small, Size.Empty, np).Width) + (i < 3 ? S(22) : 0);
                }
                if (vsego > mesto && rezhim < 2) continue;
                int x = S(18), cy = (h - S(22)) / 2;
                for (int i = 0; i < 4; i++)
                {
                    var r = new RectangleF(x, cy, S(22), S(22));
                    if (got[i]) Ui.Fill(g, r, S(11), Ui.Mix(Ui.Card, Ui.Green, 0.2), Color.Empty);
                    else if (i == tek) Ui.Fill(g, r, S(11), Ui.Akcent, Color.Empty);
                    else Ui.Fill(g, r, S(11), Ui.Card, Ui.Border);
                    Color fon = got[i] ? Ui.Mix(Ui.Card, Ui.Green, 0.2) : i == tek ? Ui.Akcent : Ui.Card;
                    TextRenderer.DrawText(g, got[i] ? "✓" : (i + 1).ToString(), Ui.SmallSemi, Rectangle.Round(r), got[i] ? Ui.Green : i == tek ? Ui.NaAkcente : Ui.Muted, fon,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | np);
                    x += S(22);
                    if (teksty[i] != "")
                    {
                        var f = i == tek ? Ui.SmallSemi : Ui.Small;
                        int tw = TextRenderer.MeasureText(teksty[i], f, Size.Empty, np).Width;
                        TextRenderer.DrawText(g, teksty[i], f, new Rectangle(x + S(7), 0, Math.Min(tw, Math.Max(0, mesto + S(18) - x - S(7))), h), i == tek ? Ui.Txt : Ui.Muted, Ui.Card,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | np);
                        x += S(7) + tw;
                    }
                    if (i < 3) { TextRenderer.DrawText(g, "›", Ui.Body, new Rectangle(x, 0, S(22), h), Ui.Faint, Ui.Card, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | np); x += S(22); }
                }
                break;
            }
        }

        // ---------- проверки ----------
        // Проверки ПК идут в фоне (WMI, драйвер, powercfg — это секунды): окно не замирает ни при запуске, ни после нажатий.
        public void Obnovit()
        {
            KatkaItog k = Demo ?? st.Poslednyaya(igra.Name);
            if (Snimok && !Fon) { proverki = Nazvat(Proverki.Sobrat(st.Fix, k, igra), igra); Perestroit(); return; }   // снимок окна — сразу и целиком
            int moe = ++pokolenie; var g = igra; var fixy = new Dictionary<string, string>(st.Fix);
            proveryayu = true;
            Perestroit();
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Proverka> r = null; bool zhelezoNovoe = false;
                lock (zamok)
                {
                    if (!Zhelezo.Gotovo) { Zhelezo.Uznat(); zhelezoNovoe = true; }
                    if (moe == pokolenie)
                    {
                        var sw = Stopwatch.StartNew();
                        try { r = Nazvat(Proverki.Sobrat(fixy, k, g), g); } catch (Exception ex) { Zhurnal.Pishi("проверки упали: " + ex); }
                        if (sw.ElapsedMilliseconds > 4000) Zhurnal.Pishi("проверки шли долго: " + sw.ElapsedMilliseconds + " мс");
                    }
                }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed) return;
                        if (zhelezoNovoe) Invalidate(new Rectangle(0, 0, Width, S(64)));
                        if (moe != pokolenie) return;
                        proveryayu = false;
                        if (r != null) proverki = r;
                        Perestroit();
                    });
                }
                catch (Exception) { }   // окно уже закрыто
            });
        }

        // сменили игру: проверки прежней игры не показываем — пусто до ответа
        void SmenitIgru(Game g) { igra = g; st.Igra = g.Name; st.Sohranit(); proverki = new List<Proverka>(); Obnovit(); }

        // Игра в левом списке — она же игра для проверок и замера. Список игр для замера — все найденные слева.
        void SinhrIgru()
        {
            Func<Game, RovnyFps.Game, bool> ta = (a, b) => a.Exes.Any(x => b.Exes.Any(y => string.Equals(Path.GetFileName(x), Path.GetFileName(y), StringComparison.OrdinalIgnoreCase)));
            var nov = st.Igry();
            foreach (var rg in rovny.Igry)
                // мерим только игры с включённым ограничением и выбранную — не всё подряд из списка RivaTuner
                if (rg.Exes.Count > 0 && (rovny.Vklyuchena(rg) || rg == rovny.Vybrana) && !nov.Any(g => ta(g, rg))) nov.Add(new Game { Name = rg.Name, Exes = rg.Exes.Select(Path.GetFileName).ToList() });
            // прежние объекты игр сохраняем: на них ссылается идущий замер
            for (int i = 0; i < nov.Count; i++) { var star = igry.FirstOrDefault(g => g.Name == nov[i].Name); if (star != null) nov[i] = star; }
            igry = nov;
            var v = rovny.Vybrana; if (v == null) return;
            var cel = igry.FirstOrDefault(g => ta(g, v));
            if (cel != null && cel.Name != igra.Name) SmenitIgru(cel);
        }

        // ---------- какие пункты показываем и как называем ----------
        // Убрано из списков по решению 15.11 (мелочи и то, что спорит с ограничением FPS). Уже применённое остаётся видно — чтобы можно было вернуть.
        internal static bool Skryt(Proverka p)
        {
            string id = p.Id;
            return id == "msi" || id == "edge" || id == "hdcp" || id == "cs2filtr" || id == "limit" || id == "store" || id == "tv:netthrot" ||
                   id.StartsWith("fastsync:") || id.StartsWith("ansel:");
        }

        // «Проверь сам»: программа видит только половину дела
        static bool ProverSam(string id) { return id == "vrr" || id == "nvoverlay" || id == "periferiya"; }

        // Названия — как в Windows, NVIDIA и играх (лист «было → стало» в макете). Меняем только у пунктов, с которыми надо что-то делать.
        internal static List<Proverka> Nazvat(List<Proverka> l, Game g)
        {
            foreach (var p in l)
            {
                if (!p.Nado) continue;
                string id = p.Id.Split(':')[0];
                switch (id)
                {
                    case "chastota": p.Nazvanie = "Частота обновления монитора"; break;
                    case "dvr": p.Nazvanie = "Xbox Game Bar: фоновая запись игры"; break;
                    case "obsl": p.Nazvanie = "Автоматическое обслуживание Windows"; break;
                    case "pitanie": p.Nazvanie = "Режим управления электропитанием"; break;
                    case "vbs": p.Nazvanie = "Изоляция ядра: целостность памяти"; break;
                    case "drayver": p.Nazvanie = "Драйвер NVIDIA старше 5 месяцев"; break;
                    case "vyvod": if (p.Nazvanie == "Кадры идут через Windows") p.Nazvanie = "Игра не в полноэкранном режиме"; break;
                    case "disk": p.Nazvanie = "Игра установлена на HDD, а не на SSD"; break;
                    case "pamyat": p.Nazvanie = "В игре кончалась оперативная память"; break;
                    case "vrr": p.Nazvanie = "FreeSync / Adaptive-Sync"; break;
                    case "nvoverlay": p.Nazvanie = "Оверлей NVIDIA App запущен"; break;
                    case "periferiya": p.Nazvanie = "Программы мыши и клавиатуры в фоне"; break;
                }
            }
            return l;
        }

        // где настройка живёт — мелкая строка над названием
        string Gde(Proverka p)
        {
            switch (p.Id.Split(':')[0])
            {
                case "chastota": return "Windows · экран";
                case "dvr": return "Windows · Xbox Game Bar";
                case "obsl": return "Windows · обслуживание";
                case "pitanie": return "NVIDIA · для " + igra.Name;
                case "fastsync": case "ansel": return "NVIDIA · для " + igra.Name;
                case "vbs": return "Windows · безопасность";
                case "hdcp": case "msi": return "NVIDIA · драйвер";
                case "rebar": return "BIOS";
                case "drayver": return "NVIDIA · драйвер";
                case "vyvod": return "В игре · по замеру";
                case "disk": return "Диск · по замеру";
                case "pamyat": return "Память · по замеру";
                case "vrr": return "Меню монитора";
                case "nvoverlay": return "NVIDIA App";
                case "periferiya": return "Фоновые программы";
                case "tv": return "Windows · осталось от оптимизаторов";
            }
            return "Windows";
        }

        // факт одной строкой: что сейчас и что станет; без оценок прироста
        internal static string Fakt(Proverka p, bool ruchnoe = false)
        {
            if (ruchnoe)
            {
                if (p.Id == "rebar") return "Видеокарта отдаёт системе " + (p.Seychas != "" ? p.Seychas : "малую часть") + " своей памяти, а не всю";
                if (p.Id == "vrr" && p.Seychas != "") return "Монитор заявляет " + p.Seychas + ". Включено ли — видно в его меню";
                if (p.Seychas != "") return "Сейчас: " + p.Seychas;
            }
            else if (p.Seychas != "" && p.Stanet != "") return "Сейчас " + p.Seychas + " → станет " + p.Stanet;
            if (p.Korotko != "") return char.ToUpper(p.Korotko[0]) + p.Korotko.Substring(1);
            int i = p.Pochemu.IndexOf(". ");
            return i > 0 ? p.Pochemu.Substring(0, i + 1) : p.Pochemu;
        }

        static string KnopkaKorotko(Proverka p)
        {
            string k = p.Knopka ?? "Применить";
            return k.Length > 11 ? k.Split(' ')[0] : k;
        }

        List<Proverka> Bezopasnye() { return proverki.Where(p => p.Mozhno && !p.Sdelano && p.Sost != Vid.Risk && !Skryt(p)).ToList(); }

        // ---------- списки второй и третьей колонок ----------
        void Perestroit()
        {
            if (rovny == null) return;
            podskazka.RemoveAll();
            geroy.Invalidate();
            var vid = proverki.Where(p => p.Sdelano || !Skryt(p)).ToList();

            // --- 2: настройки в один клик ---
            var mozhno = vid.Where(p => p.Mozhno && !p.Sdelano).ToList();
            var tv = mozhno.Where(p => p.Id.StartsWith("tv") && p.Sost != Vid.Risk).ToList();
            var odin = mozhno.Except(tv).ToList();
            var sdelano = vid.Where(p => p.Sdelano).ToList();
            var vPoryadke = vid.Where(p => !p.Nado && !p.Sdelano && p.Sost == Vid.Ok).ToList();
            relN2 = odin.Count + (tv.Count > 0 ? 1 : 0);
            zag2 = proverki.Count == 0 ? "" : relN2 > 0 ? "можно применить: " + relN2 : "всё применено";
            if (lenta2.Visible && lenta2.Width > S(60))
            {
                var s2 = new Panel { BackColor = Ui.Bg }; int w = lenta2.Shirina, y = 0;
                if (proverki.Count == 0)
                    y += Pusto(s2, y, w, proveryayu ? "Проверяю компьютер…" : "Проверок пока нет", "Смотрю настройки Windows, драйвер видеокарты и игру. Это несколько секунд.");
                else
                {
                    if (relN2 == 0) y += Pusto(s2, y, w, "Применять нечего", "Всё, что программа умеет менять сама, уже в порядке.");
                    foreach (var p in odin.Where(q => q.Sost != Vid.Risk))
                    {
                        var pp = p;
                        y += Stroka(s2, y, w, Gde(p), false, p.Nazvanie, Fakt(p), KnopkaKorotko(p), true, delegate { Ispravit(pp); }, delegate { Podrobno(pp); });
                    }
                    if (tv.Count > 0)
                    {
                        var spisok = tv.ToList();
                        Action sbros = delegate { PrimenitPachku(spisok, "Сбросить то, что осталось от оптимизаторов?", "перед сбросом остатков оптимизаторов"); };
                        y += Stroka(s2, y, w, "Windows · реестр и загрузчик", false, "Остатки программ-оптимизаторов: " + tv.Count,
                            string.Join("; ", tv.Select(p => p.Nazvanie)) + " → станет как в Windows", "Сбросить", true, sbros, sbros);
                    }
                    foreach (var p in odin.Where(q => q.Sost == Vid.Risk))
                    {
                        var pp = p;
                        y += Stroka(s2, y, w, Gde(p), true, p.Nazvanie, Fakt(p), "Подробнее", false, delegate { Podrobno(pp); }, delegate { Podrobno(pp); });
                    }
                    foreach (var p in sdelano)
                    {
                        var pp = p;
                        DateTime kogda; string kg = st.FixKogda.TryGetValue(p.Id, out kogda) ? " " + kogda.ToString("dd.MM в HH:mm") : "";
                        y += Stroka(s2, y, w, Gde(p), false, "✓ " + st.Imya(p.Id), "Применено программой" + kg + ". Прежнее значение сохранено.", "Вернуть", false, delegate { Vernut(pp); }, delegate { Podrobno(pp); });
                    }
                    if (vPoryadke.Count > 0) y += StrokaItog(s2, y + S(10), w, "✓ Уже в порядке: " + vPoryadke.Count, string.Join(", ", vPoryadke.Select(p => p.Nazvanie)),
                        delegate { Okno.Pokazat(this, "Уже в порядке", string.Join("\n", vPoryadke.Select(p => "• " + p.Nazvanie)), "Понятно", null); }) + S(10);
                }
                lenta2.Zamenit(s2);
            }
            int bez = Bezopasnye().Count;
            vseB.Text = bez > 0 ? "Применить все без риска (" + bez + ")" : "Применять нечего";
            vseB.Enabled = bez > 0; vseB.Invalidate();
            otkatB.Enabled = st.Fix.Count > 0 || st.Tochki.Count > 0; otkatB.Invalidate();

            // --- 3: сделать вручную ---
            var sov = vid.Where(p => p.Sost == Vid.Sovet && p.Id != "upor").ToList();
            var otm = sov.Where(p => st.Skip.Contains(p.Id)).ToList();
            sov = sov.Except(otm).ToList();
            var sam = sov.Where(p => ProverSam(p.Id)).ToList();
            var fakty = sov.Except(sam).ToList();
            relN3 = fakty.Count;
            zag3 = proverki.Count == 0 ? "" : "найдено: " + fakty.Count;
            if (lenta3.Visible && lenta3.Width > S(60))
            {
                var s3 = new Panel { BackColor = Ui.Bg }; int w = lenta3.Shirina, y = 0;
                if (proverki.Count == 0) y += Pusto(s3, y, w, proveryayu ? "Проверяю компьютер…" : "Проверок пока нет", "");
                else
                {
                    if (fakty.Count == 0)
                        y += Pusto(s3, y, w, "Вручную делать нечего", proverki.Any(p => p.Sost == Vid.NetDannyh) ? "Часть проверок идёт по замеру: сыграй матч, и я посмотрю ещё раз." : "По тому, что программа может проверить, всё в порядке.");
                    foreach (var p in fakty)
                    {
                        var pp = p;
                        y += Stroka(s3, y, w, Gde(p), false, p.Nazvanie, Fakt(p, true), p.Id == "rebar" ? "Как включить" : "Как сделать", false,
                            delegate { Podrobno(pp); }, delegate { Podrobno(pp); }, "Сделал", delegate { Propustit(pp); });
                    }
                    if (sam.Count > 0)
                    {
                        y += S(10);
                        var rz = new Kartochka { Zalivka = Ui.CardSel }; rz.SetBounds(0, y, w, S(28));
                        rz.Risovat = g =>
                        {
                            int zw = TextRenderer.MeasureText("ПРОВЕРЬ САМ", Nad, Size.Empty, TextFormatFlags.NoPadding).Width;
                            TextRenderer.DrawText(g, "ПРОВЕРЬ САМ", Nad, new Rectangle(S(10), 0, zw, S(27)), Ui.Txt, Ui.CardSel, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                            TextRenderer.DrawText(g, "программа этого узнать не может", Ui.Small, new Rectangle(S(18) + zw, 0, w - zw - S(28), S(27)), Ui.Muted, Ui.CardSel,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        };
                        s3.Controls.Add(rz); y += S(30);
                        foreach (var p in sam)
                        {
                            var pp = p;
                            y += Stroka(s3, y, w, Gde(p), false, p.Nazvanie, Fakt(p, true), "Как проверить", false,
                                delegate { Podrobno(pp); }, delegate { Podrobno(pp); }, "Скрыть", delegate { Propustit(pp); });
                        }
                    }
                }
                lenta3.Zamenit(s3);
            }
            sdelal.Text = otm.Count > 0 ? "Отмечено «сделал» или скрыто: " + otm.Count : "";
            sdelalPokazat.Visible = lenta3.Visible && otm.Count > 0;
            if (rel.Visible) rel.Invalidate();
            if (status.Text == "") Status(katka != null && katka.Idet ? "Идёт замер: " + katka.Igra + "…" : "Замер FPS идёт сам, пока программа открыта.", Ui.Muted);
            Invalidate();
        }

        // пустое состояние колонки: заголовок и пояснение
        int Pusto(Panel sloy, int y, int w, string zagolovok, string tekst)
        {
            int ht = tekst == "" ? 0 : Ui.Vysota(tekst, Ui.Small, w);
            var a = Ui.Tekst(zagolovok, Ui.Name, Ui.Txt); a.SetBounds(0, y + S(10), w, S(22)); a.BackColor = Ui.Bg;
            var b = Ui.Tekst(tekst, Ui.Small, Ui.Muted); b.SetBounds(0, y + S(34), w, ht + S(4)); b.BackColor = Ui.Bg;
            sloy.Controls.Add(a); sloy.Controls.Add(b);
            return S(34) + ht + S(14);
        }

        // Строка списка. Над названием — где настройка; под ним — факт; справа кнопка одной ширины, под ней — необязательная ссылка.
        // Текст рисуется самой строкой (а не надписями): так три строки стоят ровно по одной левой линии.
        int Stroka(Panel sloy, int y, int w, string gde, bool risk, string nazv, string fakt, string knopka, bool glavnaya, Action poKnopke, Action poStroke, string ssylka = null, Action poSsylke = null)
        {
            const TextFormatFlags np = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int bw = S(100), tw = w - bw - S(8);
            int odna = Vys("А", Ui.Name, tw);
            int hn = TextRenderer.MeasureText(nazv, Ui.Name, Size.Empty, np).Width <= tw ? odna : 2 * odna;   // название — до двух строк, не обрезаем       // название — до двух строк, не обрезаем
            int hf = Math.Min(Vys(fakt, Ui.Small, tw), 2 * Vys("А", Ui.Small, tw));
            int yn = S(9) + S(16), yf = yn + hn + S(2);
            int h = Math.Max(yf + hf + S(11), ssylka != null ? S(72) : S(58));
            var r = new Kartochka { Zalivka = Ui.Bg, BackColor = Ui.Bg };
            r.SetBounds(0, y, w, h);
            string gd = gde.ToUpperInvariant();
            r.Risovat = g =>
            {
                using (var pen = new Pen(Ui.Mix(Ui.Bg, Ui.Border, 0.6))) g.DrawLine(pen, 0, h - 1, w, h - 1);
                int gw = TextRenderer.MeasureText(gd, Nad, Size.Empty, np).Width;
                TextRenderer.DrawText(g, gd, Nad, new Rectangle(0, S(9), tw - (risk ? S(40) : 0), S(14)), Ui.Muted, Ui.Bg, np | TextFormatFlags.EndEllipsis);
                if (risk) TextRenderer.DrawText(g, "РИСК", Nad, new Point(Math.Min(gw, tw - S(40)) + S(8), S(9)), Ui.Red, Ui.Bg, np);
                TextRenderer.DrawText(g, nazv, Ui.Name, new Rectangle(0, yn, tw, hn), Ui.Txt, Ui.Bg, np | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, fakt, Ui.Small, new Rectangle(0, yf, tw, hf), Ui.Muted, Ui.Bg, np | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            };
            var b = new Btn(Btn.Kind.Chip, knopka) { Selected = glavnaya };
            int by = (h - S(30)) / 2 - (ssylka != null ? S(10) : 0);
            b.SetBounds(w - bw, by, bw, S(30));
            b.Click += delegate { poKnopke(); };
            r.Controls.Add(b);
            if (ssylka != null)
            {
                var l = Ui.Ssylka(ssylka); l.BackColor = Ui.Bg;
                l.Location = new Point(w - bw + (bw - TextRenderer.MeasureText(ssylka, Ui.Small).Width) / 2, by + S(34));
                l.LinkClicked += delegate { poSsylke(); };
                r.Controls.Add(l);
            }
            r.AccessibleName = nazv + ". " + fakt;
            if (poStroke != null) { r.Cursor = Cursors.Hand; r.Click += delegate { poStroke(); }; podskazka.SetToolTip(r, "Нажми, чтобы прочитать подробнее"); }
            sloy.Controls.Add(r);
            return h;
        }

        // высота текста с переносом — теми же флагами, какими он рисуется (без полей), иначе одна строка считается за две
        static int Vys(string t, Font f, int shirina)
        {
            return TextRenderer.MeasureText(string.IsNullOrEmpty(t) ? " " : t, f, new Size(shirina, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        }

        // итоговая строка «✓ Уже в порядке: N … Показать»
        int StrokaItog(Panel sloy, int y, int w, string zagolovok, string perechen, Action pokazat)
        {
            int zw = TextRenderer.MeasureText(zagolovok, Ui.SmallSemi).Width, pw = TextRenderer.MeasureText("Показать", Ui.Small).Width;
            var a = Ui.Tekst(zagolovok, Ui.SmallSemi, Ui.Green); a.SetBounds(0, y, zw + S(4), S(20)); a.BackColor = Ui.Bg;
            var b = Ui.Tekst(perechen, Ui.Small, Ui.Muted); b.SetBounds(zw + S(8), y, Math.Max(0, w - zw - pw - S(24)), S(20)); b.AutoEllipsis = true; b.BackColor = Ui.Bg;
            var l = Ui.Ssylka("Показать"); l.BackColor = Ui.Bg; l.Location = new Point(w - pw - S(2), y);
            l.LinkClicked += delegate { pokazat(); };
            sloy.Controls.Add(a); sloy.Controls.Add(b); sloy.Controls.Add(l);
            return S(22);
        }

        // ---------- действия общего окна ----------
        // пачка: «Применить все без риска» и «Сбросить остатки оптимизаторов» — сначала показать, что поменяется
        void PrimenitPachku(List<Proverka> chto, string vopros, string metka)
        {
            if (chto.Count == 0) return;
            if (!Okno.Pokazat(this, vopros, string.Join("\n", chto.Select(p => "• " + p.Nazvanie)) +
                "\n\nПрежние значения сохраню — каждый пункт потом можно вернуть.", "Применить", "Не надо")) return;
            st.SohranitTochku(metka + " (" + Punktov(chto.Count) + ")");
            int ok = 0;
            foreach (var p in chto) if (Ispravit(p, false, false, true)) ok++;
            if (ok == chto.Count) Status("Применено: " + Punktov(ok) + ". Сыграй матч — покажу «было → стало».", Ui.Green);
            Obnovit();
        }

        void PokazatSdelannoeRukami()
        {
            var otm = proverki.Where(p => p.Sost == Vid.Sovet && st.Skip.Contains(p.Id) && !Skryt(p)).ToList();
            if (otm.Count == 0) return;
            if (!Okno.Pokazat(this, "Отмечено «сделал» или скрыто", string.Join("\n", otm.Select(p => "• " + p.Nazvanie)) +
                "\n\nЕсли пункт всё ещё находится на компьютере, его можно вернуть в список.", "Вернуть в список", "Закрыть")) return;
            foreach (var p in otm) st.Skip.Remove(p.Id);
            st.Sohranit(); Perestroit();
        }

        // Один файл для пересылки: журнал ограничения FPS и журнал настроек с замером
        void PokazatZhurnal()
        {
            try
            {
                string put = Path.Combine(Zhurnal.Papka, "zhurnal-obshiy.txt");
                var sb = new StringBuilder();
                foreach (var kv in new[] { new KeyValuePair<string, string>("Ограничение FPS", RovnyFps.Zhurnal.Put), new KeyValuePair<string, string>("Настройки и замер", Zhurnal.Put) })
                {
                    sb.AppendLine("=== " + kv.Key + " — " + kv.Value + " ===");
                    try { if (File.Exists(kv.Value)) sb.AppendLine(File.ReadAllText(kv.Value)); else sb.AppendLine("(пусто)"); } catch (Exception ex) { sb.AppendLine("(не прочитался: " + ex.Message + ")"); }
                }
                Directory.CreateDirectory(Zhurnal.Papka);
                File.WriteAllText(put, sb.ToString(), new UTF8Encoding(true));
                Process.Start("explorer.exe", "/select,\"" + put + "\"");
            }
            catch (Exception ex) { Status("Журнал не открылся: " + ex.Message, Ui.Amber); }
        }
    }
}
