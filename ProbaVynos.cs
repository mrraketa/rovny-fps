// ProbaVynos.cs — проба выноса окна WinForms из оболочки (протокол «Встраивание в оболочку v1»).
// Необязательный файл рядом с Vstraivanie.cs. Повторяет то, что делает оболочка: Privet, тема, Mesto →
// (сценарий Б) другая вкладка: Mesto за экран и Spryach → назад: Mesto → «вынуть»: владелец 0 и Otpusti.
// После каждого шага кладёт в окно НОВЫЙ элемент (как программа при смене раздела) и смотрит, создала ли
// WinForms ему окно Windows и есть ли он на снимке окна (PrintWindow). Ловит ошибку 20.11: окно показано
// флагом SWP_SHOWWINDOW без WM_SHOWWINDOW — WinForms считает его спрятанным и новым элементам окон не
// создаёт, раздел пустой. Оболочка — своё спрятанное окно в этом же процессе; всё за экраном, без фокуса.
//
// Подключение в Main — ДО мьютекса «одна копия» и до запроса прав администратора:
//   if (ProbaVynos.Est(args)) { Environment.ExitCode = ProbaVynos.Prognat(() => new GlavnoeOkno()); return; }
// Сборка: этот файл в список исходников рядом с Vstraivanie.cs (/define:VSTRAIVANIE).
// Запуск — КОПИЕЙ exe с ДРУГИМ именем (иначе оболочка примет её за программу во вкладке) и с --vstroeno:
//   Программа-proba.exe --proba-vynos [папка для снимков и ITOG.txt] --vstroeno
// Код выхода — число шагов, где новое не нарисовалось (0 — всё хорошо; 100 — проба не запустилась).
#if VSTRAIVANIE
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class ProbaVynos
{
    public static bool Est(string[] args) { return Array.IndexOf(args, "--proba-vynos") >= 0; }

    static readonly List<string> stroki = new List<string>();
    static string papka;

    public static int Prognat(Func<Form> sozdat)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--proba-vynos");
        papka = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        if (papka != null) Directory.CreateDirectory(papka);
        stroki.Clear();
        int plohih = 0;
        try
        {
            if (!Vstraivanie.Flag) { Pisat("запускать с --vstroeno: без флага окно покажется само там, где его поставит программа"); return Itog(100); }
            Pisat("программа: " + Path.GetFileName(Application.ExecutablePath) + ", " + DateTime.Now.ToString("dd.MM HH:mm:ss"));
            foreach (bool smenaVkladok in new[] { false, true })
            {
                string sc = smenaVkladok ? "Б" : "А";
                Pisat("== сценарий " + sc + (smenaVkladok ? ": встало во вкладку, другая вкладка, назад, вынуть" : ": встало во вкладку, сразу вынуть"));
                using (var ob = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(50, 50), Text = "проба оболочки" })
                using (var f = sozdat())
                {
                    IntPtr hOb = ob.Handle;   // окно оболочки создано, но не показано
                    f.StartPosition = FormStartPosition.Manual;
                    if (f.WindowState != FormWindowState.Normal) f.WindowState = FormWindowState.Normal;
                    IntPtr h = f.Handle;
                    ZaEkran(h);
                    f.Show();             // с --vstroeno модуль не даёт окну показаться до первого Mesto
                    Zhdat(600);
                    // Программа могла поставить окно по центру или на запомненное место — вернуть за экран, пока
                    // оно спрятано: с этого места модуль вернёт его при выносе.
                    if (IsWindowVisible(h) || IsZoomed(h) || IsIconic(h)) { Pisat("окно показалось до оболочки или развёрнуто — проба остановлена, чтобы не вылезти на экран"); return Itog(100); }
                    ZaEkran(h);
                    var mesto = new Rectangle(-20000, -20000, 1000, 680);
                    Obolochke(h, "Privet", hOb.ToInt64(), 0);
                    Tema(h);
                    Obolochke(h, "Mesto", Para(mesto.X, mesto.Y), Para(mesto.Width, mesto.Height));
                    Zhdat(1200);
                    plohih += Osmotr(f, sc + "1 встало во вкладку", true);
                    if (smenaVkladok)
                    {
                        Obolochke(h, "Mesto", Para(-25000, -25000), Para(mesto.Width, mesto.Height));
                        Obolochke(h, "Spryach", 0, 0);
                        Zhdat(500);
                        Osmotr(f, sc + "2 другая вкладка (спрятано)", false);
                        Obolochke(h, "Mesto", Para(mesto.X, mesto.Y), Para(mesto.Width, mesto.Height));
                        Zhdat(800);
                        plohih += Osmotr(f, sc + "3 вкладка снова открыта", true);
                    }
                    // «Вынуть окно»: владельца снимает оболочка, остальное программа возвращает сама по Otpusti
                    if (GetWindow(h, 4) == hOb) SetWindowLongPtr(h, -8, IntPtr.Zero);
                    Obolochke(h, "Otpusti", 0, 0);
                    Zhdat(1500);
                    plohih += Osmotr(f, sc + "4 вынуто из оболочки", true);
                    plohih += Osmotr(f, sc + "5 ещё раз после выноса", true);
                }
                Zhdat(200);
            }
        }
        catch (Exception ex) { Pisat("сбой пробы: " + ex); return Itog(100); }
        Pisat(plohih == 0 ? "ИТОГ: новое рисуется на всех шагах" : "ИТОГ: пусто на шагах: " + plohih);
        return Itog(plohih);
    }

    // Новый элемент в окне: есть ли у него окно Windows и видно ли его на снимке окна.
    static int Osmotr(Form f, string shag, bool proverit)
    {
        IntPtr h = f.Handle;
        bool vidnoWin = IsWindowVisible(h);
        var p = new Panel { BackColor = Color.FromArgb(255, 0, 255), Bounds = new Rectangle(16, 16, 240, 140) };
        f.Controls.Add(p);
        p.BringToFront();
        Zhdat(400);
        bool estOkno = p.IsHandleCreated && IsWindowVisible(p.Handle);
        RECT w; GetWindowRect(h, out w);
        Point c = f.PointToScreen(new Point(p.Left + p.Width / 2, p.Top + p.Height / 2));
        int cvet = -1;
        for (int k = 0; k < 3 && cvet != 0xFF00FF; k++)   // снимок окна за экраном первый раз бывает чёрным
        {
            if (k > 0) Zhdat(300);
            using (var b = Snimok(h, w))
                if (b != null)
                {
                    int x = c.X - w.l, y = c.Y - w.t;
                    if (x >= 0 && y >= 0 && x < b.Width && y < b.Height) cvet = b.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                    if (papka != null && (k == 2 || cvet == 0xFF00FF)) b.Save(Path.Combine(papka, Regex.Replace(shag, @"[^\w\-]+", "-").Trim('-') + ".png"), ImageFormat.Png);
                }
        }
        int vnutri = 0;
        EnumChildWindows(h, (x, l) => { if (IsWindowVisible(x)) vnutri++; return true; }, IntPtr.Zero);
        bool ploho = proverit && vidnoWin && !(estOkno && cvet == 0xFF00FF);
        Pisat(shag + ": видно в Windows " + Da(vidnoWin) + ", видно для WinForms " + Da(f.Visible) + ", новый элемент: окно " +
              (estOkno ? "есть" : "НЕ СОЗДАНО") + ", на снимке " + (cvet == 0xFF00FF ? "есть" : cvet < 0 ? "снимка нет" : "нет (#" + cvet.ToString("x6") + ")") +
              ", видимых окон внутри " + vnutri + (ploho ? "  <- ПУСТО" : ""));
        f.Controls.Remove(p);
        p.Dispose();
        Zhdat(100);
        return ploho ? 1 : 0;
    }

    static Bitmap Snimok(IntPtr h, RECT w)
    {
        int sw = w.r - w.l, sh = w.b - w.t;
        if (sw <= 0 || sh <= 0) return null;
        var b = new Bitmap(sw, sh);
        using (var g = Graphics.FromImage(b))
        {
            IntPtr dc = g.GetHdc();
            try { PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); }   // PW_RENDERFULLCONTENT
        }
        return b;
    }

    static void ZaEkran(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        SetWindowPos(h, IntPtr.Zero, -20000, -20000, Math.Max(200, r.r - r.l), Math.Max(200, r.b - r.t), 0x10 | 0x4);   // NOACTIVATE | NOZORDER
    }

    static void Zhdat(int ms)
    {
        var t = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < t) { Application.DoEvents(); Thread.Sleep(10); }
    }

    // как оболочка: новое имя сообщения и параллельно старое
    static void Obolochke(IntPtr okno, string imya, long wp, long lp)
    {
        PostMessage(okno, RegisterWindowMessage("Vstraivanie." + imya + ".v1"), new IntPtr(wp), new IntPtr(lp));
        PostMessage(okno, RegisterWindowMessage("Korobka." + imya + ".v1"), new IntPtr(wp), new IntPtr(lp));
    }
    static long Para(int a, int b) { return ((long)(uint)a) | ((long)b << 32); }

    // тема приходит по одному значению, в конце 0 — «применяй» (тёмная, как «Сумерки»)
    static void Tema(IntPtr okno)
    {
        uint m = RegisterWindowMessage("Vstraivanie.Tema.v1");
        int[] z = { 0x1C1B22, 0x26252E, 0xE8E6F0, 0x9C99AD, 0x7C6CF0, 0xFFFFFF, 0x3A3946, 0x3FB27F, 0x6B6880, 10, 1, 0x34333E };
        for (int i = 0; i < z.Length; i++) PostMessage(okno, m, (IntPtr)(i + 1), (IntPtr)z[i]);
        PostMessage(okno, m, IntPtr.Zero, (IntPtr)z.Length);
    }

    static void Pisat(string s) { stroki.Add(s); }
    static string Da(bool b) { return b ? "да" : "нет"; }

    static int Itog(int kod)
    {
        string t = string.Join("\r\n", stroki) + "\r\n";
        if (papka != null) try { File.WriteAllText(Path.Combine(papka, "ITOG.txt"), t, new UTF8Encoding(false)); } catch { }
        try { using (var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))) o.Write(t); } catch { }
        return kod;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc f, IntPtr l);
}
#endif
