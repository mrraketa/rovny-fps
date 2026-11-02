// Vstraivanie.cs — необязательный модуль: окно программы встаёт во вкладку программы-оболочки
// по протоколу «Встраивание в оболочку v1». Модуль нейтральный и самостоятельный:
// без флага --vstroeno и без сообщений оболочки программа живёт ровно как обычно.
//
// Включается символом сборки VSTRAIVANIE (csc /define:VSTRAIVANIE). Без символа ниже
// остаются заглушки: Podklyuchit просто показывает значок в трее, Pokazat() = false.
// Нужен 64-битный процесс (x64 или anycpu без prefer32bit): место приходит в 64-битных wParam/lParam.
// C# 5, .NET Framework 4.8; ссылок на WinForms/WPF не требует — работает с HWND.
//
// Подключение (одна строка, в потоке окна, когда окно уже создано, а значок ещё НЕ показан):
//   WinForms:  Vstraivanie.Podklyuchit(Handle, v => trey.Visible = v);
//   WPF:       Vstraivanie.Podklyuchit(new WindowInteropHelper(this).EnsureHandle(), v => trey.Visible = v);
// Там, где программа сама показывает окно (второй запуск, «Показать» в трее):
//   if (Vstraivanie.Pokazat()) return;   // окно во вкладке — оболочка откроет её сама
// Второй запуск программы — перед тем как просить первую копию показаться:
//   Vstraivanie.RazreshitVperedi();
using System;
using System.Runtime.InteropServices;

#if VSTRAIVANIE
public static class Vstraivanie
{
    /// <summary>Программа запущена с флагом --vstroeno (её запустила оболочка).</summary>
    public static readonly bool Flag = EstFlag();

    /// <summary>Окно сейчас стоит во вкладке оболочки.</summary>
    public static bool VObolochke { get { return vstroeno; } }

    /// <summary>
    /// Подключить главное окно. trey(true/false) — показать/спрятать свой значок в трее;
    /// он вызывается сразу (true без флага, false с флагом), так что значок до этого не показывать.
    /// vsyaVysota — окно без прокрутки просит у оболочки всю высоту вкладки.
    /// </summary>
    public static void Podklyuchit(IntPtr hwnd, Action<bool> trey, bool vsyaVysota = false)
    {
        if (hwnd == IntPtr.Zero || okno != IntPtr.Zero) return;
        okno = hwnd;
        treyFn = trey;
        MSG_PRIVET = RegisterWindowMessage("Vstraivanie.Privet.v1");
        MSG_MESTO = RegisterWindowMessage("Vstraivanie.Mesto.v1");
        MSG_SPRYACH = RegisterWindowMessage("Vstraivanie.Spryach.v1");
        MSG_OTPUSTI = RegisterWindowMessage("Vstraivanie.Otpusti.v1");
        MSG_POKAZHI = RegisterWindowMessage("Vstraivanie.Pokazhi.v1");
        try
        {
            // Оболочка может работать с другими правами — иначе её сообщения не дойдут.
            ChangeWindowMessageFilterEx(hwnd, MSG_PRIVET, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, MSG_MESTO, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, MSG_SPRYACH, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, MSG_OTPUSTI, MSGFLT_ALLOW, IntPtr.Zero);
        }
        catch { }
        SetProp(hwnd, "Vstraivanie.Glavnoe", new IntPtr(1));
        if (vsyaVysota) SetProp(hwnd, "Vstraivanie.VsyaVysota", new IntPtr(1));
        proc = Obrabotka;
        SetWindowSubclass(hwnd, proc, ID_PODKLASSA, UIntPtr.Zero);
        if (Flag)
        {
            zhdem = true;   // окно не показываем, пока оболочка не поставит его или не пройдут 15 с
            SetTimer(hwnd, new UIntPtr(TAJMER_ZHDU), 15000, IntPtr.Zero);
            Trey(false);
        }
        else Trey(true);
    }

    /// <summary>
    /// Программу попросили показаться. true — этим займётся оболочка (или окно покажется
    /// само, когда выйдут 15 с ожидания), показывать не надо; false — показывай как обычно.
    /// </summary>
    public static bool Pokazat()
    {
        if (okno == IntPtr.Zero) return false;
        if (obolochka != IntPtr.Zero)
        {
            Pokazhi();
            return true;
        }
        return zhdem;
    }

    /// <summary>
    /// Второй запуск программы: звать перед тем, как просить первую копию показаться. Windows не даёт
    /// давно запущенной программе выйти вперёд; право может отдать тот, у кого оно есть, — второй запуск
    /// (его только что запустил человек). ASFW_ANY — первая копия передаст его дальше оболочке.
    /// </summary>
    public static void RazreshitVperedi()
    {
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
    }

    // Попросить оболочку открыть вкладку. Сначала разрешить ей выйти вперёд: сама давно запущенная
    // оболочка поднять своё окно не сможет, а у нас право есть — нас только что попросили показаться.
    static void Pokazhi()
    {
        if (obolochka == IntPtr.Zero) return;
        try
        {
            uint pid;
            GetWindowThreadProcessId(obolochka, out pid);
            if (pid != 0) AllowSetForegroundWindow(pid);
        }
        catch { }
        PostMessage(obolochka, MSG_POKAZHI, okno, IntPtr.Zero);
    }

    // ---------- состояние ----------
    static IntPtr okno, obolochka;
    static Action<bool> treyFn;
    static bool zhdem;      // флаг есть, оболочка ещё не пришла, 15 с не вышли
    static bool vstroeno;   // был первый Mesto: окно без рамки, владелец — оболочка
    static bool svoy;       // окно сейчас двигаем мы сами
    static int mx, my, mw, mh;
    static int bylStil, bylEx;
    static IntPtr bylVladelec;
    static WINDOWPLACEMENT bylPl;
    static RECT bylRect;
    static bool bylSvernut, bylRazvernut;
    static uint MSG_PRIVET, MSG_MESTO, MSG_SPRYACH, MSG_OTPUSTI, MSG_POKAZHI;
    static SUBCLASSPROC proc;   // держим ссылку, чтобы сборщик мусора не забрал делегат

    static readonly UIntPtr ID_PODKLASSA = new UIntPtr(0x56535452);
    const uint TAJMER_ZHDU = 0x5654, TAJMER_ZHIV = 0x5655;
    const int RAMKA = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000; // CAPTION THICKFRAME SYSMENU MIN MAX
    const int EX_RAMKA = 0x00040000 | 0x00000001 | 0x00000100;                      // APPWINDOW DLGMODALFRAME WINDOWEDGE
    const int WS_VISIBLE = 0x10000000;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    const uint WM_WINDOWPOSCHANGING = 0x46, WM_STYLECHANGING = 0x7C, WM_SYSCOMMAND = 0x112,
               WM_TIMER = 0x113, WM_DPICHANGED = 0x2E0, WM_NCDESTROY = 0x82;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
               SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    const int SW_HIDE = 0, SW_SHOWMINNOACTIVE = 7, SW_SHOWNA = 8, SW_SHOWNOACTIVATE = 4, SW_SHOWMAXIMIZED = 3;
    const int SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060;
    const uint MSGFLT_ALLOW = 1;
    const uint ASFW_ANY = 0xFFFFFFFF;   // (DWORD)-1: выйти вперёд разрешено любому процессу

    static bool EstFlag()
    {
        foreach (string a in Environment.GetCommandLineArgs())
            if (string.Equals(a, "--vstroeno", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void Trey(bool pokazat)
    {
        if (treyFn == null) return;
        try { treyFn(pokazat); } catch { }
    }

    // Пока окно ждёт оболочку при запуске с флагом — его не видно.
    static bool BlokPokaza { get { return Flag && !vstroeno && (zhdem || obolochka != IntPtr.Zero); } }

    static IntPtr Obrabotka(IntPtr h, uint m, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (m != 0 && m == MSG_PRIVET) { Privet(wp); return new IntPtr(1); }
            if (m != 0 && m == MSG_MESTO) { Mesto(wp, lp); return new IntPtr(1); }
            if (m != 0 && m == MSG_SPRYACH)
            {
                if (obolochka != IntPtr.Zero) { svoy = true; ShowWindow(okno, SW_HIDE); svoy = false; }
                return new IntPtr(1);
            }
            if (m != 0 && m == MSG_OTPUSTI)
            {
                if (obolochka != IntPtr.Zero) Otpustit();
                return new IntPtr(1);
            }
            switch (m)
            {
                case WM_WINDOWPOSCHANGING:
                    if (!svoy && lp != IntPtr.Zero) Dvizhenie(lp);
                    break;
                case WM_STYLECHANGING:
                    {
                        IntPtr r = DefSubclassProc(h, m, wp, lp);
                        if (!svoy && lp != IntPtr.Zero && (vstroeno || BlokPokaza))
                        {
                            int kakoy = (int)wp.ToInt64();
                            int byl = Marshal.ReadInt32(lp, 0), novy = Marshal.ReadInt32(lp, 4);
                            if (kakoy == GWL_STYLE)
                            {
                                if (vstroeno) novy &= ~RAMKA;
                                // WPF показывает окно, дописывая WS_VISIBLE в стиль, мимо WM_WINDOWPOSCHANGING.
                                if ((byl & WS_VISIBLE) == 0 && (novy & WS_VISIBLE) != 0)
                                {
                                    novy &= ~WS_VISIBLE;
                                    if (vstroeno && obolochka != IntPtr.Zero) Pokazhi();
                                }
                                Marshal.WriteInt32(lp, 4, novy);
                            }
                            else if (kakoy == GWL_EXSTYLE && vstroeno) Marshal.WriteInt32(lp, 4, novy & ~EX_RAMKA);
                        }
                        return r;
                    }
                case WM_SYSCOMMAND:
                    {
                        int sc = (int)(wp.ToInt64() & 0xFFF0);
                        if (sc == SC_CLOSE && (vstroeno || obolochka != IntPtr.Zero || zhdem))
                        {
                            svoy = true; ShowWindow(okno, SW_HIDE); svoy = false;   // крестик и Alt+F4 прячут
                            return IntPtr.Zero;
                        }
                        if (vstroeno && (sc == SC_MINIMIZE || sc == SC_MAXIMIZE || sc == SC_MOVE || sc == SC_SIZE))
                            return IntPtr.Zero;
                        break;
                    }
                case WM_TIMER:
                    {
                        ulong t = (ulong)wp.ToInt64();
                        if (t == TAJMER_ZHDU) { KillTimer(okno, new UIntPtr(TAJMER_ZHDU)); NetObolochki(); return IntPtr.Zero; }
                        if (t == TAJMER_ZHIV)
                        {
                            if (obolochka != IntPtr.Zero && !IsWindow(obolochka)) Otpustit();
                            return IntPtr.Zero;
                        }
                        break;
                    }
                case WM_DPICHANGED:
                    {
                        IntPtr r = DefSubclassProc(h, m, wp, lp);
                        if (vstroeno) Postavit(false);
                        return r;
                    }
                case WM_NCDESTROY:
                    KillTimer(h, new UIntPtr(TAJMER_ZHDU));
                    KillTimer(h, new UIntPtr(TAJMER_ZHIV));
                    RemoveProp(h, "Vstraivanie.Glavnoe");
                    RemoveProp(h, "Vstraivanie.VsyaVysota");
                    RemoveWindowSubclass(h, proc, ID_PODKLASSA);
                    okno = IntPtr.Zero; obolochka = IntPtr.Zero; zhdem = false; vstroeno = false;
                    break;
            }
        }
        catch { }
        return DefSubclassProc(h, m, wp, lp);
    }

    static void Privet(IntPtr o)
    {
        if (o == IntPtr.Zero || o == okno || !IsWindow(o)) return;
        obolochka = o;
        if (zhdem) { zhdem = false; KillTimer(okno, new UIntPtr(TAJMER_ZHDU)); }
        SetTimer(okno, new UIntPtr(TAJMER_ZHIV), 2000, IntPtr.Zero);
        if (vstroeno) SetWindowLongPtr(okno, GWLP_HWNDPARENT, o);   // новая оболочка — новый владелец
        Trey(false);
    }

    static void Mesto(IntPtr wp, IntPtr lp)
    {
        if (obolochka == IntPtr.Zero) return;   // до Privet некому быть владельцем
        long a = wp.ToInt64(), b = lp.ToInt64();
        int x = unchecked((int)(a & 0xFFFFFFFF)), y = unchecked((int)(a >> 32));
        int w = unchecked((int)(b & 0xFFFFFFFF)), hh = unchecked((int)(b >> 32));
        if (w <= 0 || hh <= 0 || w > 32000 || hh > 32000) return;
        mx = x; my = y; mw = w; mh = hh;
        if (!vstroeno) Razdet();
        Postavit(true);
    }

    // Первый Mesto: запомнить всё прежнее, снять рамку, сделать владельцем оболочку.
    static void Razdet()
    {
        svoy = true;
        bylStil = GetWindowLong(okno, GWL_STYLE);
        bylEx = GetWindowLong(okno, GWL_EXSTYLE);
        bylVladelec = GetWindowLongPtr(okno, GWLP_HWNDPARENT);
        bylPl = new WINDOWPLACEMENT();
        bylPl.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        GetWindowPlacement(okno, ref bylPl);
        GetWindowRect(okno, out bylRect);
        bylSvernut = IsIconic(okno);
        bylRazvernut = IsZoomed(okno);
        Perehody(true);
        if (bylSvernut) ShowWindow(okno, SW_SHOWNOACTIVATE);       // развернуть из свёрнутого без фокуса
        else if (bylRazvernut)
        {
            WINDOWPLACEMENT p = bylPl;
            p.flags = 0; p.showCmd = SW_SHOWNOACTIVATE;
            SetWindowPlacement(okno, ref p);
        }
        // Спрятать на время смены — так панель задач убирает кнопку окна.
        if (IsWindowVisible(okno)) ShowWindow(okno, SW_HIDE);
        vstroeno = true;
        SetWindowLong(okno, GWL_STYLE, GetWindowLong(okno, GWL_STYLE) & ~RAMKA);
        SetWindowLong(okno, GWL_EXSTYLE, GetWindowLong(okno, GWL_EXSTYLE) & ~EX_RAMKA);
        SetWindowLongPtr(okno, GWLP_HWNDPARENT, obolochka);
        svoy = false;
    }

    static void Postavit(bool pokazat)
    {
        svoy = true;
        IntPtr bylDpi = IntPtr.Zero;
        try { bylDpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }   // per-monitor v2
        if (IsIconic(okno)) ShowWindow(okno, SW_SHOWNOACTIVATE);
        SetWindowPos(okno, IntPtr.Zero, mx, my, mw, mh,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED | (pokazat ? SWP_SHOWWINDOW : 0));
        try { if (bylDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(bylDpi); } catch { }
        svoy = false;
    }

    // Otpusti или оболочка пропала: всё как было до встраивания.
    static void Otpustit()
    {
        KillTimer(okno, new UIntPtr(TAJMER_ZHIV));
        bool neStoyalo = !vstroeno;
        if (vstroeno)
        {
            svoy = true;
            bool vidno = IsWindowVisible(okno);
            if (vidno) ShowWindow(okno, SW_HIDE);   // панель задач вернёт кнопку при показе
            vstroeno = false;
            SetWindowLongPtr(okno, GWLP_HWNDPARENT, bylVladelec);
            SetWindowLong(okno, GWL_STYLE, (GetWindowLong(okno, GWL_STYLE) & ~RAMKA) | (bylStil & RAMKA));
            SetWindowLong(okno, GWL_EXSTYLE, (GetWindowLong(okno, GWL_EXSTYLE) & ~EX_RAMKA) | (bylEx & EX_RAMKA));
            if (bylSvernut)
            {
                WINDOWPLACEMENT p = bylPl; p.showCmd = SW_SHOWMINNOACTIVE; SetWindowPlacement(okno, ref p);
            }
            else if (bylRazvernut)
            {
                WINDOWPLACEMENT p = bylPl; p.showCmd = vidno ? SW_SHOWMAXIMIZED : SW_HIDE; SetWindowPlacement(okno, ref p);
            }
            else
                SetWindowPos(okno, IntPtr.Zero, bylRect.l, bylRect.t, bylRect.r - bylRect.l, bylRect.b - bylRect.t,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED | (vidno ? SWP_SHOWWINDOW : 0));
            Perehody(false);
            svoy = false;
        }
        obolochka = IntPtr.Zero;
        zhdem = false;
        Trey(true);
        // С флагом окно ждало оболочку спрятанным, а она отпустила его (или пропала) раньше первого
        // Mesto — как без оболочки за 15 с: показать, иначе окно так и останется невидимым.
        if (Flag && neStoyalo) PokazatBezFokusa();
    }

    static void PokazatBezFokusa()
    {
        svoy = true; ShowWindow(okno, SW_SHOWNA); svoy = false;   // без фокуса: он может быть в игре
    }

    // 15 с с флагом, а Privet не пришёл: оболочки нет — работаем как обычно.
    static void NetObolochki()
    {
        if (!zhdem) return;
        zhdem = false;
        Trey(true);
        PokazatBezFokusa();
    }

    // Чужие попытки сдвинуть или показать окно.
    static void Dvizhenie(IntPtr lp)
    {
        WINDOWPOS p = (WINDOWPOS)Marshal.PtrToStructure(lp, typeof(WINDOWPOS));
        bool izm = false;
        if ((p.flags & SWP_SHOWWINDOW) != 0)
        {
            if (BlokPokaza) { p.flags = (p.flags & ~SWP_SHOWWINDOW) | SWP_NOACTIVATE; izm = true; }
            else if (vstroeno && !IsWindowVisible(okno))
            {
                // Программа хочет вылезти сама — вместо этого просим оболочку открыть вкладку.
                p.flags = (p.flags & ~SWP_SHOWWINDOW) | SWP_NOACTIVATE; izm = true;
                if (obolochka != IntPtr.Zero) Pokazhi();
            }
        }
        if (vstroeno)
        {
            if ((p.flags & SWP_NOMOVE) == 0 && (p.x != mx || p.y != my)) { p.x = mx; p.y = my; izm = true; }
            if ((p.flags & SWP_NOSIZE) == 0 && (p.cx != mw || p.cy != mh)) { p.cx = mw; p.cy = mh; izm = true; }
        }
        if (izm) Marshal.StructureToPtr(p, lp, false);
    }

    static void Perehody(bool vyklyuchit)
    {
        try { int v = vyklyuchit ? 1 : 0; DwmSetWindowAttribute(okno, 3 /*DWMWA_TRANSITIONS_FORCEDISABLED*/, ref v, 4); } catch { }
    }

    static IntPtr GetWindowLongPtr(IntPtr h, int i)
    { return IntPtr.Size == 8 ? GetWindowLongPtr64(h, i) : new IntPtr(GetWindowLong(h, i)); }
    static IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v)
    { return IntPtr.Size == 8 ? SetWindowLongPtr64(h, i, v) : new IntPtr(SetWindowLong(h, i, v.ToInt32())); }

    delegate IntPtr SUBCLASSPROC(IntPtr h, uint m, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }

    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SUBCLASSPROC p, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr h, SUBCLASSPROC p, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint m, uint a, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string s, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr h, string s);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr f);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
}
#else
// Сборка без встраивания: программа ведёт себя ровно как раньше.
public static class Vstraivanie
{
    public static readonly bool Flag = false;
    public static bool VObolochke { get { return false; } }
    public static void Podklyuchit(IntPtr hwnd, Action<bool> trey, bool vsyaVysota = false) { if (trey != null) trey(true); }
    public static bool Pokazat() { return false; }
    public static void RazreshitVperedi() { }
}
#endif
