// FPS-мастер: находит, что режет FPS на этом ПК, советует и по кнопке исправляет; меряет катки до и после.
// Входит в «Ровный FPS» с версии 11: средняя и правая колонки общего окна.
// Сборка: build.bat (csc из .NET Framework 4.x, C# 5). Стиль окна — как у «Ровного FPS».
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
    // ---------------- Состояние ----------------
    class Sostoyanie
    {
        public List<Game> Svoi = new List<Game>();
        public List<KatkaItog> Katki = new List<KatkaItog>();
        public Dictionary<string, string> Fix = new Dictionary<string, string>();   // id → что вернуть
        public Dictionary<string, DateTime> FixKogda = new Dictionary<string, DateTime>();
        public HashSet<string> Skip = new HashSet<string>();
        public bool Spiskom, PmOtkaz;
        public string Igra;
        string put;

        // Точка отката: какие исправления были сделаны на этот момент. Откат к точке = вернуть сделанное после неё
        // (и включить снова то, что было сделано тогда, а потом возвращено). Снимков реестра нет — работает тот же
        // проверенный путь «исправить / вернуть», что и у кнопок.
        public class Tochka { public DateTime Kogda; public string Metka = ""; public List<string> Ids = new List<string>(); }
        public List<Tochka> Tochki = new List<Tochka>();
        public Dictionary<string, string> Imena = new Dictionary<string, string>();   // id → название исправления, для окна точек
        const int MaxTochek = 12;

        // false — точно такое состояние уже сохранено (вторую одинаковую точку не плодим)
        public bool SohranitTochku(string metka)
        {
            var ids = Fix.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (Tochki.Any(t => t.Ids.SequenceEqual(ids))) return false;
            Tochki.Add(new Tochka { Kogda = DateTime.Now, Metka = (metka ?? "").Replace('\t', ' ').Replace('\n', ' '), Ids = ids });
            while (Tochki.Count > MaxTochek) Tochki.RemoveAt(0);
            Sohranit();
            Zhurnal.Pishi("точка отката: " + metka + " (исправлений: " + ids.Count + ")");
            return true;
        }

        // t == null — «до программы». Что вернуть — от свежего к старому.
        public List<string> ChtoVernut(Tochka t)
        {
            var nado = new HashSet<string>(t == null ? new List<string>() : t.Ids);
            return Fix.Keys.Where(id => !nado.Contains(id)).OrderByDescending(id => FixKogda.ContainsKey(id) ? FixKogda[id] : DateTime.MinValue).ToList();
        }

        public List<string> ChtoVklyuchit(Tochka t) { return t == null ? new List<string>() : t.Ids.Where(id => !Fix.ContainsKey(id)).ToList(); }

        public string Imya(string id) { string n; return Imena.TryGetValue(id, out n) ? n : id; }

        public static Sostoyanie Zagruzit(string put)
        {
            var s = new Sostoyanie { put = put };
            string[] lines;
            try { lines = File.Exists(put) ? File.ReadAllLines(put, Encoding.UTF8) : new string[0]; }
            catch (IOException) { lines = new string[0]; }
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                try
                {
                    if (p[0] == "svoya" && p.Length >= 3) s.Svoi.Add(new Game { Name = p[1], Exes = { p[2] }, Svoya = true });
                    else if (p[0] == "katka") { var k = KatkaItog.IzStroki(p); if (k != null) s.Katki.Add(k); }
                    else if (p[0] == "fix" && p.Length >= 4) { s.Fix[p[1]] = p[3]; s.FixKogda[p[1]] = new DateTime(long.Parse(p[2])); }
                    else if (p[0] == "skip" && p.Length >= 2) s.Skip.Add(p[1]);
                    else if (p[0] == "imya" && p.Length >= 3) s.Imena[p[1]] = p[2];
                    else if (p[0] == "tochka" && p.Length >= 4)
                        s.Tochki.Add(new Tochka { Kogda = new DateTime(long.Parse(p[1])), Metka = p[2], Ids = p[3].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList() });
                    else if (p[0] == "spiskom") s.Spiskom = true;
                    else if (p[0] == "pm_otkaz") s.PmOtkaz = true;
                    else if (p[0] == "igra" && p.Length >= 2) s.Igra = p[1];
                }
                catch (Exception) { }   // битая строка — пропускаем
            }
            return s;
        }

        public void Sohranit()
        {
            var sb = new StringBuilder();
            foreach (var g in Svoi) sb.Append("svoya\t" + g.Name + "\t" + g.Exes[0] + "\n");
            foreach (var k in Katki.Skip(Math.Max(0, Katki.Count - 200))) sb.Append(k.VStroku() + "\n");
            foreach (var kv in Fix) sb.Append("fix\t" + kv.Key + "\t" + FixKogda[kv.Key].Ticks + "\t" + kv.Value + "\n");
            foreach (var x in Skip) sb.Append("skip\t" + x + "\n");
            foreach (var kv in Imena) sb.Append("imya\t" + kv.Key + "\t" + kv.Value.Replace('\t', ' ') + "\n");
            foreach (var t in Tochki) sb.Append("tochka\t" + t.Kogda.Ticks + "\t" + t.Metka + "\t" + string.Join("|", t.Ids) + "\n");
            if (Spiskom) sb.Append("spiskom\n");
            if (PmOtkaz) sb.Append("pm_otkaz\n");
            if (Igra != null) sb.Append("igra\t" + Igra + "\n");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(put));
                string tmp = put + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(put)) File.Replace(tmp, put, null); else File.Move(tmp, put);
            }
            catch (Exception ex) { Zhurnal.Pishi("состояние не сохранилось: " + ex.Message); }
        }

        public List<Game> Igry()
        {
            var r = new List<Game> { new Game { Name = "Deadlock", Exes = { "deadlock.exe" } }, new Game { Name = "CS2", Exes = { "cs2.exe" } } };
            r.AddRange(Svoi);
            return r;
        }

        public KatkaItog Poslednyaya(string igra)
        {
            return Katki.Where(k => k.Igra == igra).OrderBy(k => k.Kogda).LastOrDefault();
        }

        // «было» — последняя катка до первого исправления, «стало» — последняя после последнего
        public void ByloStalo(string igra, out KatkaItog bylo, out KatkaItog stalo)
        {
            bylo = stalo = null;
            var moi = Katki.Where(k => k.Igra == igra).OrderBy(k => k.Kogda).ToList();
            if (FixKogda.Count == 0 || moi.Count == 0) return;
            DateTime pervoe = FixKogda.Values.Min(), poslednee = FixKogda.Values.Max();
            bylo = moi.LastOrDefault(k => k.Kogda < pervoe);
            stalo = moi.LastOrDefault(k => k.Kogda > poslednee);
        }
    }

    static class Core
    {
        public static int MonitorHz() { return Proverki.TekushayaHz(); }
    }

    // ---------------- Вид (как у «Ровного FPS») ----------------
    static class Ui
    {
        // Палитра — одна на всё окно. Тема оболочки («Коробка» присылает свои цвета) подменяет её целиком;
        // без оболочки (Tema == null) — ровно свой вид. Смысловые цвета (зелёный/жёлтый/красный) — свои,
        // на светлых темах темнее, чтобы читались на светлом фоне.
        public static Color Bg, Card, CardHover, CardSel, Border, BorderSel, Txt, Muted, Faint, Green, Amber, Red,
            Txt2, Gluhoy,                                   // текст пояснений, приглушённая подложка
            Akcent, NaAkcente, AkcentHover, AkcentDown;     // главная кнопка и выбранный чип
        public static int Rad;                              // скругление кнопок, px при 100 %
        public static TemaObolochki Tema;                   // какая тема сейчас (null — своя)
        static Ui() { Primenit(null); }

        static Color C(int rgb) { return Color.FromArgb(255, Color.FromArgb(rgb)); }
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }
        static Color[] Tek() { return new[] { Bg, Card, CardHover, CardSel, Border, BorderSel, Txt, Muted, Faint, Green, Amber, Red, Txt2, Gluhoy }; }

        // Возвращает, какой цвет каким стал, — чтобы перекрасить уже созданные элементы (Perekrasit)
        public static Dictionary<int, Color> Primenit(TemaObolochki t)
        {
            var bylo = Bg.IsEmpty ? null : Tek();
            Tema = t;
            if (t == null)
            {
                Bg = Color.FromArgb(14, 14, 16); Card = Color.FromArgb(24, 24, 27); CardHover = Color.FromArgb(32, 32, 36);
                CardSel = Color.FromArgb(39, 39, 44); Border = Color.FromArgb(58, 58, 64); BorderSel = Color.FromArgb(96, 96, 104);
                Txt = Color.FromArgb(244, 244, 245); Muted = Color.FromArgb(150, 150, 158); Faint = Color.FromArgb(82, 82, 91);
                Green = Color.FromArgb(74, 222, 128); Amber = Color.FromArgb(251, 191, 36); Red = Color.FromArgb(248, 113, 113);
                Txt2 = Color.FromArgb(214, 214, 218); Gluhoy = Color.FromArgb(18, 18, 20);
                Akcent = Txt; NaAkcente = Bg; AkcentHover = Color.White; AkcentDown = Color.FromArgb(212, 212, 216);
                Rad = 10;
            }
            else
            {
                Bg = C(t.Fon); Card = C(t.Poverhnost); CardSel = C(t.Navedenie); CardHover = Mix(Card, CardSel, 0.5);
                Border = C(t.Liniya); Txt = C(t.Tekst); Muted = C(t.Vtorichnyy); BorderSel = Mix(Border, Txt, 0.35);
                Faint = Mix(Muted, Bg, 0.45); Txt2 = Mix(Txt, Muted, 0.25); Gluhoy = Mix(Bg, Card, 0.4);
                Akcent = C(t.Akcent); NaAkcente = C(t.NaAkcente);
                AkcentHover = Mix(Akcent, t.Temnaya ? Color.White : Color.Black, 0.1); AkcentDown = Mix(Akcent, t.Temnaya ? Color.Black : Color.Black, t.Temnaya ? 0.18 : 0.2);
                if (t.Temnaya) { Green = Color.FromArgb(74, 222, 128); Amber = Color.FromArgb(251, 191, 36); Red = Color.FromArgb(248, 113, 113); }
                else { Green = Color.FromArgb(21, 128, 61); Amber = Color.FromArgb(180, 83, 9); Red = Color.FromArgb(200, 40, 40); }
                Rad = Math.Max(2, Math.Min(18, t.Radius));
            }
            var map = new Dictionary<int, Color>();
            if (bylo != null)
            {
                var stalo = Tek();
                for (int i = 0; i < bylo.Length; i++) if (!map.ContainsKey(bylo[i].ToArgb())) map[bylo[i].ToArgb()] = stalo[i];
            }
            return map;
        }

        // Перекрасить уже созданные элементы: каждый цвет старой палитры — в его цвет новой
        public static void Perekrasit(Control c, Dictionary<int, Color> map)
        {
            Color n;
            if (map.TryGetValue(c.BackColor.ToArgb(), out n)) c.BackColor = n;
            if (map.TryGetValue(c.ForeColor.ToArgb(), out n)) c.ForeColor = n;
            var l = c as LinkLabel;
            if (l != null)
            {
                if (map.TryGetValue(l.LinkColor.ToArgb(), out n)) l.LinkColor = n;
                if (map.TryGetValue(l.ActiveLinkColor.ToArgb(), out n)) l.ActiveLinkColor = n;
                if (map.TryGetValue(l.VisitedLinkColor.ToArgb(), out n)) l.VisitedLinkColor = n;
            }
            var kt = c as Kartochka; if (kt != null) { Color z = kt.Zalivka; if (map.TryGetValue(z.ToArgb(), out n)) kt.Zalivka = n; }
            foreach (Control d in c.Controls) Perekrasit(d, map);
            c.Invalidate();
        }

        // Темы «Коробки» (korobka\TEMY.md) — только для снимков (--shot … tema=id): так окно проверяется без оболочки
        public static TemaObolochki ProbnayaTema(string id)
        {
            switch (id)
            {
                case "klassika": return T(0x151517, 0x1F1F22, 0xF4F4F5, 0x9E9EA6, 0xFFFFFF, 0x09090A, 0x323234, 0xF4F4F5, 0x4C4C52, 0x343437, 10, true);
                case "grafit": return T(0x2B2D31, 0x313338, 0xF2F3F5, 0x949BA4, 0x5AA2FF, 0x0A1626, 0x4E5054, 0x3FB56B, 0x5D6068, 0x44464A, 4, true);
                case "sumerki": return T(0x2C3443, 0x333C4C, 0xEBE1CB, 0xA3A9B5, 0xCB5A3F, 0xEBE1CB, 0x585C63, 0xA9BC4D, 0x5F6979, 0x454C58, 8, true);
                case "bumaga": return T(0xECE5D4, 0xF3EEE2, 0x2E251C, 0x75664F, 0xB6472F, 0xF3EEE2, 0xB7B0A1, 0x7C9530, 0xADA18C, 0xD2C7AF, 8, false);
                case "svetloe": return T(0xF5F7FA, 0xFFFFFF, 0x1A1C21, 0x5F6980, 0x2563EB, 0xFFFFFF, 0xD6D8DB, 0x1F9A56, 0xB4BBC8, 0xD7DCE6, 9, false);
                case "pastel": return T(0xFCF7FB, 0xFFFFFF, 0x473B53, 0x786A86, 0x8F62C9, 0xFFFFFF, 0xE6E0E7, 0x4FB088, 0xD1C3DB, 0xE6D6EB, 15, false);
            }
            return null;
        }
        static TemaObolochki T(int fon, int pov, int tek, int vt, int ak, int na, int li, int vkl, int vykl, int nav, int r, bool temn)
        {
            return new TemaObolochki { Fon = fon, Poverhnost = pov, Tekst = tek, Vtorichnyy = vt, Akcent = ak, NaAkcente = na, Liniya = li, Vkl = vkl, Vykl = vykl, Navedenie = nav, Radius = r, Temnaya = temn };
        }
        public static float K = 1f;
        public static int S(float v) { return (int)Math.Round(v * K); }
        public static readonly Font Title = new Font("Segoe UI Semibold", 15.5f), Body = new Font("Segoe UI", 10f), Name = new Font("Segoe UI Semibold", 10.5f),
            Small = new Font("Segoe UI", 8.75f), SmallSemi = new Font("Segoe UI Semibold", 8.75f), Value = new Font("Segoe UI Semibold", 16f),
            Head = new Font("Segoe UI Semibold", 13.5f), Button = new Font("Segoe UI Semibold", 10.5f);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color fill, Color border)
        {
            using (var p = Round(r, rad))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border != Color.Empty) using (var pen = new Pen(border)) g.DrawPath(pen, p);
            }
        }

        public static Color Cvet(Vid v) { return v == Vid.Ok ? Green : v == Vid.Risk ? Red : v == Vid.NetDannyh ? Faint : Amber; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // тёмная полоса заголовка, как всё окно (Windows 10 2004+ и 11; на старых — молча ничего)
        public static void TemnyyZagolovok(IntPtr h)
        {
            try { int v = 1; if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4); } catch (Exception) { }
        }

        // высота текста с переносом по словам
        public static int Vysota(string t, Font f, int shirina)
        {
            return TextRenderer.MeasureText(string.IsNullOrEmpty(t) ? " " : t, f, new Size(shirina, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        public static LinkLabel Ssylka(string t)
        {
            var l = new LinkLabel { Text = t, Font = Small, AutoSize = true, LinkColor = Muted, ActiveLinkColor = Txt, VisitedLinkColor = Muted,
                LinkBehavior = LinkBehavior.HoverUnderline, Cursor = Cursors.Hand, BackColor = Color.Transparent };
            return l;
        }

        public static Label Tekst(string t, Font f, Color c)
        {
            return new Label { Text = t, Font = f, ForeColor = c, AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false };
        }
    }

    // Кнопка своей отрисовки: основная (белая), второстепенная (тёмная) и чип
    class Btn : Control
    {
        public enum Kind { Primary, Secondary, Chip }
        readonly Kind kind;
        public bool Selected;
        bool hover, down;

        public Btn(Kind k, string text)
        {
            kind = k; Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton; AccessibleName = text;
        }

        public int Shirina { get { return TextRenderer.MeasureText(Text, kind == Kind.Chip ? Ui.SmallSemi : Ui.Button).Width + Ui.S(kind == Kind.Chip ? 26 : 34); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            Color under = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(under);
            Color fill, text, border = Color.Empty;
            switch (kind)
            {
                case Kind.Primary:
                    fill = !Enabled ? Ui.CardSel : down ? Ui.AkcentDown : hover ? Ui.AkcentHover : Ui.Akcent;
                    text = Enabled ? Ui.NaAkcente : Ui.Muted; break;
                case Kind.Chip:
                    fill = Selected ? Ui.Akcent : down ? Ui.CardSel : hover ? Ui.CardHover : Ui.CardSel;
                    text = Selected ? Ui.NaAkcente : hover ? Ui.Txt : Ui.Muted; break;
                default:
                    fill = down ? Ui.CardSel : hover ? Ui.CardHover : Ui.CardSel;
                    text = Enabled ? Ui.Txt : Ui.Faint; border = hover ? Ui.BorderSel : Color.Empty; break;
            }
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = kind == Kind.Chip ? Height / 2f : Ui.S(Ui.Rad);
            Ui.Fill(g, r, rad, fill, border);
            if (Focused && ShowFocusCues) using (var p = Ui.Round(new RectangleF(2, 2, Width - 5, Height - 5), rad - 2)) using (var pen = new Pen(Color.FromArgb(120, Ui.Green))) g.DrawPath(pen, p);
            TextRenderer.DrawText(g, Text, kind == Kind.Chip ? Ui.SmallSemi : Ui.Button, new Rectangle(0, 0, Width, Height), text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Тёмная скруглённая подложка; рисовать поверх — через Risovat
    class Kartochka : Panel
    {
        public Action<Graphics> Risovat;
        public Color Zalivka = Ui.Card;
        public Kartochka()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            Ui.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(Ui.Rad + 2), Zalivka, Color.Empty);
            if (Risovat != null) Risovat(g);
        }
    }

    // Небольшое окно-вопрос в стиле программы
    class Okno : Form
    {
        public static bool Pokazat(IWin32Window owner, string title, string text, string ok, string cancel)
        {
            using (var f = new Okno(title, text, ok, cancel)) return f.ShowDialog(owner) == DialogResult.OK;
        }

        Okno(string title, string text, string ok, string cancel)
        {
            Func<float, int> S = v => Ui.S(v);
            Text = "Ровный FPS"; Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Txt;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            int w = S(400), x = S(22), y = S(18);
            var t = Ui.Tekst(title, Ui.Name, Ui.Txt); t.SetBounds(x, y, w - 2 * x, S(26)); Controls.Add(t); y += S(34);
            int h = TextRenderer.MeasureText(text, Ui.Small, new Size(w - 2 * x, 1000), TextFormatFlags.WordBreak).Height;
            var b = Ui.Tekst(text, Ui.Small, Ui.Txt); b.SetBounds(x, y, w - 2 * x, h + S(4)); Controls.Add(b); y += h + S(20);
            var bOk = new Btn(Btn.Kind.Primary, ok);
            bOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            if (cancel != null)
            {
                var bNo = new Btn(Btn.Kind.Secondary, cancel);
                bNo.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                int bw = (w - 2 * x - S(10)) / 2;
                bOk.SetBounds(x, y, bw, S(40)); bNo.SetBounds(x + bw + S(10), y, bw, S(40)); Controls.Add(bNo);
            }
            else bOk.SetBounds(x, y, w - 2 * x, S(40));
            Controls.Add(bOk);
            ClientSize = new Size(w, y + S(58));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            Shown += delegate { bOk.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.TemnyyZagolovok(Handle); }
    }

    // Окно «Точки отката»: список сохранённых состояний и откат к любому
    class OknoTochek : Form
    {
        public Sostoyanie.Tochka Vybrana;   // к какой точке откатить (null при Otkat — «до программы»)
        public bool Otkat, Sohranit, Windows;

        public OknoTochek(Sostoyanie st)
        {
            Func<float, int> S = v => Ui.S(v);
            Text = "Ровный FPS"; Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Txt;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            int w = S(440), x = S(22), y = S(18), rw = w - 2 * x;
            var zag = Ui.Tekst("Точки отката", Ui.Head, Ui.Txt); zag.SetBounds(x, y, rw, S(28)); Controls.Add(zag); y += S(34);
            string poyasn = "Перед каждым изменением программа запоминает, что было. Откат возвращает настройки Windows и драйвера к выбранной точке.";
            int hp = Ui.Vysota(poyasn, Ui.Small, rw);
            var pt = Ui.Tekst(poyasn, Ui.Small, Ui.Muted); pt.SetBounds(x, y, rw, hp + S(4)); Controls.Add(pt); y += hp + S(14);

            // точка без исправлений — то же, что «До программы»: отдельной строкой не показываем
            var stroki = st.Tochki.AsEnumerable().Reverse().Where(t => t.Ids.Count > 0).Take(7).Select(t => new { T = t, Verh = t.Kogda.ToString("dd.MM HH:mm") + " · " + t.Metka }).ToList();
            if (st.Fix.Count > 0) stroki.Add(new { T = (Sostoyanie.Tochka)null, Verh = "До программы" });
            if (stroki.Count == 0)
            {
                var pusto = Ui.Tekst("Пока откатывать нечего: программа ещё ничего не меняла. Первая точка появится сама — перед первым исправлением.", Ui.Small, Ui.Txt);
                int hh = Ui.Vysota(pusto.Text, Ui.Small, rw); pusto.SetBounds(x, y, rw, hh + S(4)); Controls.Add(pusto); y += hh + S(14);
            }
            foreach (var s in stroki)
            {
                var t = s.T;
                int vernut = st.ChtoVernut(t).Count, vkl = st.ChtoVklyuchit(t).Count;
                string niz = vernut == 0 && vkl == 0 ? "как сейчас" :
                    (vernut > 0 ? "вернёт " + MainForm.Punktov(vernut) : "") + (vernut > 0 && vkl > 0 ? " · " : "") + (vkl > 0 ? "включит снова " + MainForm.Punktov(vkl) : "");
                if (t == null) niz = "вернёт всё, что исправила программа: " + MainForm.Punktov(vernut);
                var r = new Kartochka(); r.SetBounds(x, y, rw, S(52));
                int pravee = rw - S(14);
                if (vernut > 0 || vkl > 0)
                {
                    var b = new Btn(Btn.Kind.Chip, "Откатить") { Selected = true };
                    b.SetBounds(rw - b.Shirina - S(10), S(12), b.Shirina, S(28));
                    b.Click += delegate { Vybrana = t; Otkat = true; DialogResult = DialogResult.OK; Close(); };
                    r.Controls.Add(b); pravee = b.Left;
                }
                var l1 = Ui.Tekst(s.Verh, Ui.Body, Ui.Txt); l1.SetBounds(S(14), S(7), pravee - S(22), S(20)); l1.AutoEllipsis = true;
                var l2 = Ui.Tekst(niz, Ui.Small, Ui.Muted); l2.SetBounds(S(14), S(27), pravee - S(22), S(18)); l2.AutoEllipsis = true;
                l1.BackColor = l2.BackColor = Ui.Card;
                r.Controls.Add(l1); r.Controls.Add(l2); Controls.Add(r); y += S(58);
            }
            // Точка восстановления Windows из окна убрана.
            y += S(6);
            var bS = new Btn(Btn.Kind.Secondary, "Сохранить точку сейчас"); var bZ = new Btn(Btn.Kind.Secondary, "Закрыть");
            int bw = (rw - S(10)) / 2;
            bS.SetBounds(x, y, bw + S(30), S(40)); bZ.SetBounds(x + bw + S(40), y, bw - S(30), S(40));
            bS.Click += delegate { Sohranit = true; DialogResult = DialogResult.OK; Close(); };
            bZ.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(bS); Controls.Add(bZ);
            ClientSize = new Size(w, y + S(58));
            KeyPreview = true;
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            Shown += delegate { bZ.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.TemnyyZagolovok(Handle); }
    }

    // ---------------- Главное окно ----------------
    // ---------------- Картинка-отчёт для мастера ----------------
    static class Otchet
    {
        static readonly Font F1 = new Font("Segoe UI Semibold", 17f, GraphicsUnit.Pixel), F2 = new Font("Segoe UI", 13f, GraphicsUnit.Pixel),
            F3 = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel), Fv = new Font("Segoe UI Semibold", 26f, GraphicsUnit.Pixel),
            Fh = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel), Fs = new Font("Segoe UI Semibold", 11.5f, GraphicsUnit.Pixel),
            Fk = new Font("Consolas", 17f, FontStyle.Bold, GraphicsUnit.Pixel);

        // текст сглаживанием в оттенках серого: ClearType на картинке даёт цветную кайму (цифры синели)
        static void T(Graphics g, string s, Font f, Color c, int x, int y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x - 2, y - 1); }
        static void Tr(Graphics g, string s, Font f, Color c, int right, int y) { int w = (int)g.MeasureString(s, f).Width; T(g, s, f, c, right - w + 4, y); }

        public static Bitmap Narisovat(string igra, KatkaItog k, List<Proverka> proverki)
        {
            var tema = Ui.Tema;
            if (tema == null) return Narisovat2(igra, k, proverki);
            Ui.Primenit(null);
            try { return Narisovat2(igra, k, proverki); }
            finally { Ui.Primenit(tema); }
        }

        static Bitmap Narisovat2(string igra, KatkaItog k, List<Proverka> proverki)
        {
            int W = 760, X = 28, R = W - 28;
            int H = 300 + (k != null ? 190 : 60) + proverki.Count * 42 + 40;
            var bmp = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Ui.Bg);
                // шапка
                Ui.Fill(g, new RectangleF(X, 26, 44, 44), 11, Ui.CardSel, Color.Empty);
                using (var b = new SolidBrush(Ui.Green)) g.FillPolygon(b, new[] { new PointF(X + 24, 34), new PointF(X + 24, 46), new PointF(X + 34, 46), new PointF(X + 20, 64), new PointF(X + 20, 52), new PointF(X + 10, 52) });
                T(g, "Отчёт «Ровный FPS»", Fh, Ui.Txt, X + 58, 24);
                T(g, DateTime.Now.ToString("dd.MM.yyyy HH:mm") + " · " + Zhelezo.Windows + " · версия " + Program.Versiya, F2, Ui.Muted, X + 58, 52);
                string kod = Kod();
                Tr(g, "номер отчёта", Fs, Ui.Muted, R, 28); Tr(g, kod, Fk, Ui.Txt, R, 46);
                // железо
                int y = 92, cw = (R - X - 10) / 2;
                var zh = new[] { new[] { "Процессор", Zhelezo.Cpu }, new[] { "Видеокарта", Zhelezo.Gpu + (Zhelezo.Drayver != "" ? " · драйвер " + Zhelezo.Drayver : "") },
                    new[] { "Память", Zhelezo.OzuGb + " ГБ" + (k != null && k.MinSvobodnoMb >= 0 ? " · свободно " + Itog.Chislo(k.MinSvobodnoMb / 1024.0) + " ГБ в игре" : "") },
                    new[] { "Экран", Proverki.Pokazat(Proverki.TekushayaHz()) + " Гц" } };
                for (int i = 0; i < 4; i++)
                {
                    int x = X + (i % 2) * (cw + 10), yy = y + (i / 2) * 72;
                    Ui.Fill(g, new RectangleF(x, yy, cw, 62), 12, Ui.Card, Color.Empty);
                    T(g, zh[i][0], F2, Ui.Muted, x + 14, yy + 10); T(g, zh[i][1], F3, Ui.Txt, x + 14, yy + 30);
                }
                y += 152;
                // катка
                if (k != null)
                {
                    T(g, ("КАТКА · " + igra + " · " + Math.Round(k.SekundVIgre / 60.0, 1).ToString("0.#") + " МИН В БОЮ · " + k.Kogda.ToString("dd.MM HH:mm")).ToUpperInvariant(), Fs, Ui.Muted, X + 2, y);
                    y += 22;
                    var ch = new[] { new[] { "средний FPS", Itog.Chislo(k.Sredniy) }, new[] { "1% low", Itog.Chislo(k.Low1) },
                        new[] { "рывков в минуту", Itog.Chislo(k.Ryvki) }, new[] { "держит FPS", Itog.Chislo(k.Derzhit) } };
                    int bw = (R - X - 30) / 4;
                    for (int i = 0; i < 4; i++)
                    {
                        int x = X + i * (bw + 10);
                        Ui.Fill(g, new RectangleF(x, y, bw, 72), 12, Ui.Card, Color.Empty);
                        T(g, ch[i][0], F2, Ui.Muted, x + 14, y + 10); T(g, ch[i][1], Fv, Ui.Txt, x + 12, y + 30);
                    }
                    y += 88;
                    var pr = k.PoPrichinam(); int vsego = Math.Max(1, pr.Sum(x => x.Key));
                    T(g, ("ОТКУДА ПОДВИСАНИЯ (" + k.Podvisaniy + " ЗА КАТКУ)").ToUpperInvariant(), Fs, Ui.Muted, X + 2, y); y += 22;
                    if (pr.Count == 0) { T(g, "подвисаний не было", F2, Ui.Txt, X + 2, y); y += 26; }
                    else
                    {
                        float x0 = X; Color[] cv = { Ui.Amber, Ui.Red, Color.FromArgb(96, 165, 250), Ui.Muted };
                        for (int i = 0; i < pr.Count; i++)
                        {
                            float wi = (R - X) * pr[i].Key / (float)vsego;
                            using (var b = new SolidBrush(cv[Math.Min(i, 3)])) g.FillRectangle(b, x0, y, wi, 10);
                            x0 += wi;
                        }
                        y += 18;
                        T(g, string.Join("  ·  ", pr.Select(x => x.Key + " — " + x.Value)), F2, Ui.Txt2, X + 2, y); y += 28;
                    }
                }
                else { T(g, "Замера " + igra + " ещё нет — сыграй матч при открытой программе.", F2, Ui.Muted, X + 2, y); y += 40; }
                // проверки
                T(g, "ПРОВЕРКИ", Fs, Ui.Muted, X + 2, y + 6); y += 30;
                foreach (var p in proverki)
                {
                    Ui.Fill(g, new RectangleF(X, y, R - X, 36), 10, Ui.Card, Color.Empty);
                    using (var b = new SolidBrush(p.Sdelano ? Ui.Green : Ui.Cvet(p.Sost))) g.FillEllipse(b, X + 14, y + 14, 8, 8);
                    T(g, p.Nazvanie, F2, Ui.Txt, X + 32, y + 9);
                    Tr(g, p.Sdelano ? "исправлено программой" : p.Korotko, F2, Ui.Muted, R - 14, y + 9);
                    y += 42;
                }
                T(g, "Без имён, паролей и файлов — только железо и замер.", Fs, Ui.Faint, X + 2, y + 6);
            }
            return bmp;
        }

        static string Kod()
        {
            const string a = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var r = new Random(); var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) { if (i == 3) sb.Append('-'); sb.Append(a[r.Next(a.Length)]); }
            return sb.ToString();
        }
    }

    // ---------------- Запуск ----------------
}
