// Nvidia.cs — профиль драйвера NVIDIA для одной игры (как «Программные настройки» в панели NVIDIA), через nvapi64.dll.
// Только одна настройка — «Режим управления электропитанием: предпочтителен режим максимальной производительности»,
// и только для выбранной игры, НЕ глобально (гайд Max.mov 5:58: глобальные параметры ломали другие игры).
// Зачем: под лимитом кадров или в лёгкой игре видеокарта сбрасывает частоту — кадр готовится дольше (гайд 6:02).
// Структуры — по nvapi.h (NVDRS_SETTING_V1 = 0x3020 байт, NVDRS_PROFILE_V1, NVDRS_APPLICATION_V1).
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FpsMaster
{
    static class Nvidia
    {
        public const uint PitanieId = 0x1057EB71;   // PREFERRED_PSTATE_ID
        public const uint MaxProizv = 1;            // PREFERRED_PSTATE_PREFER_MAX

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D0();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DOut(out IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D1(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DFindApp(IntPtr s, IntPtr name, out IntPtr prof, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DFindProf(IntPtr s, IntPtr name, out IntPtr prof);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DCreateProf(IntPtr s, IntPtr info, out IntPtr prof);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DCreateApp(IntPtr s, IntPtr prof, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DGet(IntPtr s, IntPtr prof, uint id, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DSet(IntPtr s, IntPtr prof, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DDelSet(IntPtr s, IntPtr prof, uint id);

        static T F<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero) throw new InvalidOperationException("в драйвере NVIDIA нет функции 0x" + id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        const int Str = 4096, SettingSize = 0x3020, ProfileSize = 4 + Str + 16, AppSize = 8 + Str * 3;
        const int OffId = 4 + Str, OffType = OffId + 4, OffLoc = OffType + 4, OffPredef = OffLoc + 4, OffCur = OffPredef + 8 + 4100;

        static IntPtr Bufer(int n) { var p = Marshal.AllocHGlobal(n); var z = new byte[n]; Marshal.Copy(z, 0, p, n); return p; }
        static void Stroka(IntPtr p, int off, string s) { var b = Encoding.Unicode.GetBytes(s + "\0"); Marshal.Copy(b, 0, p + off, Math.Min(b.Length, Str - 2)); }
        static void Proverit(int rc, string chto) { if (rc != 0) throw new InvalidOperationException("NVIDIA: " + chto + " — код " + rc); }

        public static bool Est
        {
            get { try { return QueryInterface(0x0150E828) != IntPtr.Zero; } catch (Exception) { return false; } }
        }

        // Сессия с загруженными настройками драйвера; save — записать изменения
        static void Sessiya(Action<IntPtr> rabota, bool save)
        {
            Proverit(F<D0>(0x0150E828)(), "Initialize");
            IntPtr s; Proverit(F<DOut>(0x0694D52E)(out s), "CreateSession");
            try
            {
                Proverit(F<D1>(0x375DBD6B)(s), "LoadSettings");
                rabota(s);
                if (save) Proverit(F<D1>(0xFCBC7E14)(s), "SaveSettings");
            }
            finally { F<D1>(0xDAD9CFF8)(s); }
        }

        static IntPtr NaytiProfil(IntPtr s, string exe)
        {
            IntPtr name = Bufer(Str), app = Bufer(AppSize);
            try
            {
                Stroka(name, 0, exe);
                Marshal.WriteInt32(app, AppSize | (1 << 16));
                IntPtr prof;
                int rc = F<DFindApp>(0xEEE566B2)(s, name, out prof, app);
                return rc == 0 ? prof : IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
        }

        // Значение настройки, заданное пользователем в профиле игры; null — не задано (стоит умолчание драйвера)
        static uint? Znachenie(IntPtr s, IntPtr prof, uint id)
        {
            IntPtr st = Bufer(SettingSize);
            try
            {
                Marshal.WriteInt32(st, SettingSize | (1 << 16));
                if (F<DGet>(0x73BF8338)(s, prof, id, st) != 0) return null;
                bool predef = Marshal.ReadInt32(st, OffPredef) != 0;
                int loc = Marshal.ReadInt32(st, OffLoc);   // 0 — задано в этом профиле
                if (predef || loc != 0) return null;
                return (uint)Marshal.ReadInt32(st, OffCur);
            }
            finally { Marshal.FreeHGlobal(st); }
        }

        static void Zadat(IntPtr s, IntPtr prof, uint id, uint v)
        {
            IntPtr st = Bufer(SettingSize);
            try
            {
                Marshal.WriteInt32(st, SettingSize | (1 << 16));
                Marshal.WriteInt32(st, OffId, (int)id);
                Marshal.WriteInt32(st, OffType, 0);   // NVDRS_DWORD_TYPE
                Marshal.WriteInt32(st, OffCur, (int)v);
                Proverit(F<DSet>(0x577DD202)(s, prof, st), "SetSetting");
            }
            finally { Marshal.FreeHGlobal(st); }
        }

        public const uint VsyncId = 0x00A879CF, FastSync = 0x18888888;   // VSYNCMODE_ID, VSYNCMODE_VIRTUAL
        public const uint AnselId = 0x1035DB89;                          // ANSEL_ALLOW_ID: 0 — запрещён

        // Значение настройки в профиле игры, заданное пользователем; null — не задано или профиля нет
        public static uint? ZnachenieIgry(string exe, uint id)
        {
            uint? r = null;
            Sessiya(s => { var p = NaytiProfil(s, exe); if (p != IntPtr.Zero) r = Znachenie(s, p, id); }, false);
            return r;
        }

        // Режим питания видеокарты для exe: true — «максимальная производительность»
        public static bool MaxPitanie(string exe)
        {
            bool r = false;
            Sessiya(s => { var p = NaytiProfil(s, exe); r = p != IntPtr.Zero && Znachenie(s, p, PitanieId) == MaxProizv; }, false);
            return r;
        }

        // Ставит «максимальную производительность» для exe. Возвращает, что вернуть: "new" — профиль создан нами,
        // "-" — в профиле значения не было, иначе прежнее число.
        public static string PostavitMax(string exe, string igra) { return PostavitIgre(exe, igra, PitanieId, MaxProizv); }

        // Ставит настройку id = v в профиль игры. Возвращает, что вернуть: "new" — профиль создан нами,
        // "-" — значения не было, иначе прежнее число.
        public static string PostavitIgre(string exe, string igra, uint id, uint v)
        {
            Rezerv();
            string old = null;
            Sessiya(s =>
            {
                var p = NaytiProfil(s, exe);
                if (p == IntPtr.Zero)
                {
                    IntPtr info = Bufer(ProfileSize), app = Bufer(AppSize);
                    try
                    {
                        Marshal.WriteInt32(info, ProfileSize | (1 << 16));
                        Stroka(info, 4, "FPS-мастер · " + igra);
                        Proverit(F<DCreateProf>(0xCC176068)(s, info, out p), "CreateProfile");
                        Marshal.WriteInt32(app, AppSize | (1 << 16));
                        Stroka(app, 8, exe);
                        Proverit(F<DCreateApp>(0x4347A9DE)(s, p, app), "CreateApplication");
                    }
                    finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(app); }
                    old = "new";
                }
                else { var bylo = Znachenie(s, p, id); old = bylo.HasValue ? bylo.Value.ToString() : "-"; }
                Zadat(s, p, id, v);
            }, true);
            Zhurnal.Pishi("NVIDIA: 0x" + id.ToString("X8") + " = 0x" + v.ToString("X") + " для " + exe + " (вернуть: " + old + ")");
            return old;
        }

        public static void Vernut(string exe, string old) { VernutIgre(exe, PitanieId, old); }

        // Профиль, созданный нами, не удаляем: в нём могут быть другие наши настройки — снимаем только эту
        public static void VernutIgre(string exe, uint id, string old)
        {
            Sessiya(s =>
            {
                var p = NaytiProfil(s, exe);
                if (p == IntPtr.Zero) return;
                if (old == "new" || old == "-") F<DDelSet>(0xE4A26362)(s, p, id);
                else Zadat(s, p, id, uint.Parse(old));
            }, true);
            Zhurnal.Pishi("NVIDIA: 0x" + id.ToString("X8") + " для " + exe + " возвращено (" + old + ")");
        }

        // ---------- глобальный профиль (волна 5: след старого ограничителя Profile Inspector) ----------
        public const uint StaryyLimitId = 0x10834FEE;   // FRL_FPS — ограничитель кадров до V3, есть только в XML Profile Inspector
        // Драйвер 621.01 (09.11.2026) этот ID не знает: SetSetting → -160 (настройка не найдена). Значит, там он и не действует,
        // а чтение вернёт «не задано» — карточки не будет. След ищем на тех драйверах, что его ещё понимают.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DGlob(IntPtr s, out IntPtr prof);

        static IntPtr Globalnyy(IntPtr s) { IntPtr p; Proverit(F<DGlob>(0x617BFF9F)(s, out p), "GetCurrentGlobalProfile"); return p; }

        // Значение, заданное пользователем в глобальном профиле; null — не задано
        public static uint? ZnachenieGlobal(uint id)
        {
            uint? r = null;
            Sessiya(s => r = Znachenie(s, Globalnyy(s), id), false);
            return r;
        }

        // Снять настройку в глобальном профиле. Возвращает прежнее значение (для «вернуть»).
        public static string SnyatGlobal(uint id)
        {
            Rezerv();
            string old = "-";
            Sessiya(s => { var p = Globalnyy(s); var v = Znachenie(s, p, id); if (v.HasValue) { old = v.Value.ToString(); F<DDelSet>(0xE4A26362)(s, p, id); } }, true);
            Zhurnal.Pishi("NVIDIA: 0x" + id.ToString("X8") + " снят в глобальном профиле (было " + old + ")");
            return old;
        }

        public static void VernutGlobal(uint id, string old)
        {
            if (old == "-") return;
            Sessiya(s => Zadat(s, Globalnyy(s), id, uint.Parse(old)), true);
            Zhurnal.Pishi("NVIDIA: 0x" + id.ToString("X8") + " в глобальном профиле возвращён (" + old + ")");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D1Prof(IntPtr s, IntPtr prof);

        // Резерв базы профилей драйвера перед первым изменением
        public static void Rezerv()
        {
            try
            {
                string drs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"NVIDIA Corporation\Drs");
                string kuda = System.IO.Path.Combine(Zhurnal.Papka, "drs-rezerv");
                if (System.IO.Directory.Exists(kuda) || !System.IO.Directory.Exists(drs)) return;
                System.IO.Directory.CreateDirectory(kuda);
                foreach (var f in System.IO.Directory.GetFiles(drs, "nvdrsdb*.bin"))
                    System.IO.File.Copy(f, System.IO.Path.Combine(kuda, System.IO.Path.GetFileName(f)));
                Zhurnal.Pishi("резерв профилей NVIDIA: " + kuda);
            }
            catch (Exception ex) { Zhurnal.Pishi("резерв профилей NVIDIA не сделан: " + ex.Message); }
        }

        // Самопроверка БЕЗ записи: в сессии создаётся пробный профиль, ставится и читается настройка,
        // а сессия закрывается без SaveSettings — на диск драйвера ничего не попадает.
        public static string ProbaBezZapisi()
        {
            string r = null;
            Sessiya(s =>
            {
                IntPtr p, info = Bufer(ProfileSize), app = Bufer(AppSize);
                try
                {
                    Marshal.WriteInt32(info, ProfileSize | (1 << 16));
                    Stroka(info, 4, "FPS-master proba");
                    Proverit(F<DCreateProf>(0xCC176068)(s, info, out p), "CreateProfile");
                    Marshal.WriteInt32(app, AppSize | (1 << 16));
                    Stroka(app, 8, "fpsmaster-proba.exe");
                    Proverit(F<DCreateApp>(0x4347A9DE)(s, p, app), "CreateApplication");
                    var naydeno = NaytiProfil(s, "fpsmaster-proba.exe");
                    var do_ = Znachenie(s, p, PitanieId);
                    Zadat(s, p, PitanieId, MaxProizv);
                    var posle = Znachenie(s, p, PitanieId);
                    F<DDelSet>(0xE4A26362)(s, p, PitanieId);
                    var snyato = Znachenie(s, p, PitanieId);
                    Zadat(s, p, VsyncId, FastSync); var fs = Znachenie(s, p, VsyncId);
                    Zadat(s, p, AnselId, 0); var an = Znachenie(s, p, AnselId);
                    string frl;   // старый ограничитель в глобальном профиле — тоже только в памяти сессии
                    try { var gp = Globalnyy(s); Zadat(s, gp, StaryyLimitId, 60); var f = Znachenie(s, gp, StaryyLimitId); F<DDelSet>(0xE4A26362)(s, gp, StaryyLimitId); frl = (f.HasValue ? f.ToString() : "умолч.") + "→" + (Znachenie(s, gp, StaryyLimitId).HasValue ? "осталось" : "снято"); }
                    catch (Exception ex) { frl = "ошибка: " + ex.Message; }
                    r ="профиль найден по exe: " + (naydeno != IntPtr.Zero) + "; до: " + (do_.HasValue ? do_.ToString() : "умолч.") +
                        "; после: " + (posle.HasValue ? posle.ToString() : "умолч.") + "; снято: " + (snyato.HasValue ? snyato.ToString() : "умолч.") +
                        "; fastsync: " + (fs == FastSync) + "; ansel: " + (an.HasValue ? an.ToString() : "умолч.") + "; старый лимит глобально: " + frl;
                }
                finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(app); }
            }, false);
            return r;
        }
    }
}
