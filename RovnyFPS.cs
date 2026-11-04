// Ровный FPS: ставит игре лимит кадров через RivaTuner (режим NVIDIA Reflex),
// как в гайде Pqqip Production (youtube.com/watch?v=EHgBdsNGjvM). Сборка: build.bat (csc из .NET Framework 4.x, C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RovnyFps
{
    class Game
    {
        public string Key;      // appid Steam, "custom:<путь к exe>" или "<магазин>:<id>" (epic:, gog:, ea:, ubi:, bnet:, riot:)
        public string AppId;    // null для игр не из Steam
        public string Store;    // подпись магазина в списке ("Epic", "GOG"…); null — Steam или своя игра
        public string Name;
        public List<string> Exes = new List<string>();
    }

    // ---------------- Состояние: что включено нами и что было до нас ----------------
    class State
    {
        public Dictionary<string, int> On = new Dictionary<string, int>();         // key -> fps
        public Dictionary<string, string> OnExes = new Dictionary<string, string>(); // key -> exe1;exe2
        public Dictionary<string, int> Fps = new Dictionary<string, int>();        // запомненный лимит
        public Dictionary<string, string> Orig = new Dictionary<string, string>(); // "appid|account" -> прежние LaunchOptions ("-" = не было)
        public List<string> Custom = new List<string>();
        public HashSet<string> Ssync = new HashSet<string>();                      // «Без разрывов» (Scanline Sync)
        string path;

        public static State Load(string path)
        {
            var s = new State(); s.path = path;
            string[] lines;
            try { lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : new string[0]; }
            catch (IOException) { lines = new string[0]; }
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                try
                {
                    if (p[0] == "on" && p.Length >= 4) { s.On[p[1]] = int.Parse(p[2]); s.OnExes[p[1]] = p[3]; }
                    else if (p[0] == "fps" && p.Length >= 3) s.Fps[p[1]] = int.Parse(p[2]);
                    else if (p[0] == "orig" && p.Length >= 3) s.Orig[p[1]] = p[2];
                    else if (p[0] == "custom" && p.Length >= 2) s.Custom.Add(p[1]);
                    else if (p[0] == "ssync" && p.Length >= 2) s.Ssync.Add(p[1]);
                }
                catch (Exception) { } // битая строка — пропускаем, остальное читаем
            }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in On) sb.Append("on\t" + kv.Key + "\t" + kv.Value + "\t" + OnExes[kv.Key] + "\n");
            foreach (var kv in Fps) sb.Append("fps\t" + kv.Key + "\t" + kv.Value + "\n");
            foreach (var kv in Orig) sb.Append("orig\t" + kv.Key + "\t" + kv.Value + "\n");
            foreach (var c in Custom) sb.Append("custom\t" + c + "\n");
            foreach (var k in Ssync) sb.Append("ssync\t" + k + "\n");
            Disk.Write(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    static class Disk
    {
        // Сначала во временный файл, потом подмена: при выключении питания не останется обрезанного файла
        public static void Write(string path, string text, Encoding enc)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, enc);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // ---------------- Мини-редактор VDF (localconfig.vdf) ----------------
    static class Vdf
    {
        class Tok { public char Kind; public string Val; public int Start, End; } // 's' — строка в кавычках, '{', '}'

        static List<Tok> Lex(string t)
        {
            var r = new List<Tok>();
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '"')
                {
                    var sb = new StringBuilder(); int st = i; i++;
                    while (i < t.Length && t[i] != '"')
                    {
                        if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i] == 'n' ? '\n' : t[i] == 't' ? '\t' : t[i]); }
                        else sb.Append(t[i]);
                        i++;
                    }
                    i++;
                    r.Add(new Tok { Kind = 's', Val = sb.ToString(), Start = st, End = i });
                }
                else if (c == '{' || c == '}') { r.Add(new Tok { Kind = c, Start = i, End = i + 1 }); i++; }
                else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
                else i++;
            }
            return r;
        }

        static string Esc(string v) { return v.Replace("\\", "\\\\").Replace("\"", "\\\""); }

        // Ищет блок по пути ключей (без учёта регистра). Возвращает индексы токенов '{' и '}' или -1.
        static bool FindBlock(List<Tok> toks, string[] path, out int open, out int close)
        {
            open = close = -1;
            var stack = new List<string>();
            var opens = new List<int>();
            for (int i = 0; i < toks.Count; i++)
            {
                var k = toks[i];
                if (k.Kind == 's' && i + 1 < toks.Count && toks[i + 1].Kind == '{')
                {
                    stack.Add(k.Val); opens.Add(i + 1); i++;
                    continue;
                }
                if (k.Kind == 's' && i + 1 < toks.Count && toks[i + 1].Kind == 's') { i++; continue; }
                if (k.Kind == '}' && stack.Count > 0)
                {
                    if (stack.Count == path.Length && Same(stack, path)) { open = opens[opens.Count - 1]; close = i; return true; }
                    stack.RemoveAt(stack.Count - 1); opens.RemoveAt(opens.Count - 1);
                }
            }
            return false;
        }

        static bool Same(List<string> a, string[] b)
        {
            for (int i = 0; i < b.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // Прямой потомок-значение key внутри блока [open, close]
        static int FindValue(List<Tok> toks, int open, int close, string key)
        {
            int depth = 0;
            for (int i = open + 1; i < close; i++)
            {
                var k = toks[i];
                if (k.Kind == '{') depth++;
                else if (k.Kind == '}') depth--;
                else if (depth == 0 && k.Kind == 's' && i + 1 < close && toks[i + 1].Kind == 's')
                {
                    if (string.Equals(k.Val, key, StringComparison.OrdinalIgnoreCase)) return i + 1;
                    i++;
                }
            }
            return -1;
        }

        static readonly string[] AppsPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "Apps" };

        public static string GetLaunchOptions(string text, string appId)
        {
            var toks = Lex(text);
            int o, c;
            if (!FindBlock(toks, AppsPath.Concat(new[] { appId }).ToArray(), out o, out c)) return null;
            int v = FindValue(toks, o, c, "LaunchOptions");
            return v < 0 ? null : toks[v].Val;
        }

        public static string SetLaunchOptions(string text, string appId, string value)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var toks = Lex(text);
            int o, c;
            if (FindBlock(toks, AppsPath.Concat(new[] { appId }).ToArray(), out o, out c))
            {
                int v = FindValue(toks, o, c, "LaunchOptions");
                if (v >= 0) return text.Substring(0, toks[v].Start) + "\"" + Esc(value) + "\"" + text.Substring(toks[v].End);
                string ind = IndentOf(text, toks[c].Start) + "\t";
                int at = LineStart(text, toks[c].Start);
                return text.Insert(at, ind + "\"LaunchOptions\"\t\t\"" + Esc(value) + "\"" + nl);
            }
            if (!FindBlock(toks, AppsPath, out o, out c)) throw new Exception("В localconfig.vdf нет раздела Apps");
            string ind2 = IndentOf(text, toks[c].Start) + "\t";
            int at2 = LineStart(text, toks[c].Start);
            string block = ind2 + "\"" + appId + "\"" + nl + ind2 + "{" + nl
                         + ind2 + "\t\"LaunchOptions\"\t\t\"" + Esc(value) + "\"" + nl + ind2 + "}" + nl;
            return text.Insert(at2, block);
        }

        static int LineStart(string t, int pos) { return t.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1; }
        static string IndentOf(string t, int pos) { int s = LineStart(t, pos); return t.Substring(s, pos - s); }
    }

    // ---------------- Вся работа с RivaTuner и Steam ----------------
    class Core
    {
        public string RtssDir, ProfilesDir, SteamDir, StatePath;
        // Источники игр других лаунчеров; самопроверка подменяет их подложными данными
        public string EpicManifestsDir;
        public Func<List<Dictionary<string, string>>> GogSource, UninstallSource, UbiSource;
        public bool TestMode;
        public State St;
        public Action<string> Trace = delegate { };

        // Параметры запуска CS2 из гайда (-console не нужен, он только для записи видео у автора)
        public static readonly string[][] Cs2Flags = {
            new[] { "-mainthreadpriority", "3" }, new[] { "-high" }, new[] { "-noreflex" },
            new[] { "+fps_max", "0" }, new[] { "-allow_third_party_software" } };

        // Exe, который рисует картинку (не лаунчер). Источник — research\igry.md (30.10.2026):
        // конфиги запуска Steam, PCGamingWiki, файлы игр на диске. Угадывание по размеру ошибается
        // на Source 2 (vconsole2.exe больше игры) и Cyberpunk (scc.exe), поэтому известные — списком.
        static readonly Dictionary<string, string[]> KnownExes = new Dictionary<string, string[]> {
            { "730", new[] { "cs2.exe" } }, { "570", new[] { "dota2.exe" } }, { "1422450", new[] { "deadlock.exe" } },
            { "1086940", new[] { "bg3.exe", "bg3_dx11.exe" } }, { "1091500", new[] { "Cyberpunk2077.exe" } },
            { "578080", new[] { "TslGame.exe" } }, { "1172470", new[] { "r5apex_dx12.exe", "r5apex.exe" } },
            { "252490", new[] { "RustClient.exe" } }, { "271590", new[] { "GTA5.exe" } }, { "3240220", new[] { "GTA5_Enhanced.exe" } },
            { "2767030", new[] { "Marvel-Win64-Shipping.exe" } }, { "1938090", new[] { "cod.exe" } }, { "2357570", new[] { "Overwatch.exe" } },
            { "359550", new[] { "RainbowSix.exe" } }, { "252950", new[] { "RocketLeague.exe" } }, { "553850", new[] { "helldivers2.exe" } },
            { "3932890", new[] { "EscapeFromTarkov.exe" } }, { "2073850", new[] { "Discovery.exe" } }, { "2807960", new[] { "bf6.exe" } },
            { "1808500", new[] { "PioneerGame.exe" } }, { "2507950", new[] { "DeltaForceClient-Win64-Shipping.exe" } },
            { "1245620", new[] { "eldenring.exe" } }, { "2622380", new[] { "nightreign.exe" } }, { "1551360", new[] { "ForzaHorizon5.exe" } },
            { "2483190", new[] { "forzahorizon6.exe" } }, { "3321460", new[] { "CrimsonDesert.exe" } }, { "236390", new[] { "aces.exe" } },
            { "1407200", new[] { "WorldOfTanks.exe" } } };

        // Человек выбрал вручную лаунчер — подставляем настоящий exe игры
        public static readonly Dictionary<string, string> LauncherToGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "FortniteLauncher.exe", "FortniteClient-Win64-Shipping.exe" }, { "rust.exe", "RustClient.exe" },
            { "RocketLeague_EAC.exe", "RocketLeague.exe" }, { "PlayGTAV.exe", "GTA5.exe" }, { "GTAVLauncher.exe", "GTA5.exe" },
            { "MarvelRivals_Launcher.exe", "Marvel-Win64-Shipping.exe" }, { "BsgLauncher.exe", "EscapeFromTarkov.exe" },
            { "VALORANT.exe", "VALORANT-Win64-Shipping.exe" }, { "LeagueClient.exe", "League of Legends.exe" },
            { "df_launcher.exe", "DeltaForceClient-Win64-Shipping.exe" }, { "LariLauncher.exe", "bg3.exe" } };

        // Что сказать человеку про конкретную игру (по имени exe)
        public static string WarningFor(Game g)
        {
            foreach (var e in g.Exes)
            {
                var n = e.ToLowerInvariant();
                if (n == "cs2.exe") return "Допишу -allow_third_party_software — Valve пишет, что он может снизить Trust Factor";
                if (n == "valorant-win64-shipping.exe" || n == "league of legends.exe") return "Vanguard: с RivaTuner бывают вылеты — на свой риск";
                if (n.StartsWith("r5apex") || n == "bf6.exe") return "Античит EA иногда не пускает RivaTuner — если лимит не держится, это он";
                if (n == "eldenring.exe" || n == "nightreign.exe") return "Игра сама не даёт больше 60 FPS";
                if (n == "gta5_enhanced.exe") return "Выше 140 FPS игра статтерит — ставь не больше 140";
                if (n == "javaw.exe") return "Лимит подействует на все Java-программы, не только на Minecraft";
            }
            return null;
        }

        public static int? FpsCeilingFor(Game g)
        {
            foreach (var e in g.Exes)
            {
                var n = e.ToLowerInvariant();
                if (n == "eldenring.exe" || n == "nightreign.exe" || n == "genshinimpact.exe" || n == "yuanshen.exe") return 60;
                if (n == "gta5_enhanced.exe") return 140;
            }
            return null;
        }

        static readonly HashSet<string> NotGames = new HashSet<string> {
            "228980", "365670", "431960", "3515100", "1070560", "1391110", "1628350", "1493710", "250820" };
        static readonly Regex NotGameName = new Regex(@"Redistributable|Runtime|Proton|SDK|Dedicated Server|Soundtrack|Wallpaper Engine|^Blender$|^OCCT$|SteamVR", RegexOptions.IgnoreCase);
        static readonly Regex BadExe = new Regex(@"crash|unins|redist|setup|prereq|report|helper|launcher|anticheat|battleye|^be_|install|update|service|cefprocess|webhelper|dotnet|dxsetup|7z|python|node|ffmpeg|steamerror|bugsplat|uploader|cleanup|elevat|benchmark|editor|modtool|vc_?redist|^start_protected|vconsole|^scc\.exe$|redmod|bootstrapper|^wgcs|^beac_|_eac\.exe$|df_launcher|^larilauncher", RegexOptions.IgnoreCase);

        public Core()
        {
            FindRtss();
            var s = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            SteamDir = s != null ? (s.GetValue("SteamPath") as string ?? "").Replace('/', '\\') : null;
            if (string.IsNullOrEmpty(SteamDir) || !Directory.Exists(SteamDir))
            {
                // окно UAC с паролем другого админа: HKCU чужой, берём общий путь установки
                var m = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                SteamDir = m != null ? m.GetValue("InstallPath") as string : null;
            }
            // в папке пользователя, а не рядом с exe: exe можно запускать хоть из «Загрузок», хоть из архива
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RovnyFPS");
            Directory.CreateDirectory(dir);
            StatePath = Path.Combine(dir, "sostoyanie.txt");

            // Epic хранит манифесты в ProgramData; папку данных можно перенести — тогда она в реестре
            EpicManifestsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var e = b.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher"))
                {
                    var p = e != null ? e.GetValue("AppDataPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "Manifests"))) EpicManifestsDir = Path.Combine(p, "Manifests");
                }
            }
            catch { }
            GogSource = () => ReadRegKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games");
            UbiSource = () => ReadRegKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs");
            UninstallSource = () =>
            {
                const string un = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
                var all = ReadRegKeys(RegistryHive.LocalMachine, RegistryView.Registry64, un);
                all.AddRange(ReadRegKeys(RegistryHive.LocalMachine, RegistryView.Registry32, un));
                all.AddRange(ReadRegKeys(RegistryHive.CurrentUser, RegistryView.Registry64, un));
                return all;
            };
        }

        void FindRtss()
        {
            var r = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS");
            RtssDir = r != null ? r.GetValue("InstallDir") as string : null;
            if (RtssDir == null) RtssDir = @"C:\Program Files (x86)\RivaTuner Statistics Server";
            ProfilesDir = Path.Combine(RtssDir, "Profiles");
        }

        public void LoadState() { St = State.Load(StatePath); }
        public bool RtssInstalled { get { return File.Exists(Path.Combine(RtssDir, "RTSS.exe")); } }

        const string RtssPage = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";

        // Ставит RivaTuner через winget (официальный пакет Guru3D.RTSS), без окон и вопросов
        void InstallRtss()
        {
            Process p;
            try
            {
                // без --disable-interactivity: старый winget его не знает и сразу падает
                p = Process.Start(new ProcessStartInfo("winget",
                    "install --id Guru3D.RTSS -e --silent --accept-package-agreements --accept-source-agreements")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Process.Start(RtssPage);
                throw new Exception("Не смог поставить RivaTuner сам — открыл страницу, поставь его и нажми ещё раз.");
            }
            // читаем оба потока параллельно, иначе winget может застрять на полном буфере
            p.OutputDataReceived += delegate { }; p.ErrorDataReceived += delegate { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(10 * 60 * 1000))
            {
                try { p.Kill(); } catch { }
                Process.Start(RtssPage);
                throw new Exception("Установка RivaTuner зависла (10 минут). Открыл страницу — поставь его сам и нажми ещё раз.");
            }
            FindRtss();
            if (!RtssInstalled)
            {
                Process.Start(RtssPage);
                throw new Exception("RivaTuner не поставился (код " + p.ExitCode + ") — открыл страницу, поставь его и нажми ещё раз.");
            }
        }

        // Старые RivaTuner (из старых Afterburner) не знают лимитер через Reflex
        public bool RtssTooOldForReflex()
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(Path.Combine(RtssDir, "RTSS.exe"));
                if (v.FileMajorPart == 0) return false; // версии нет — не знаем, не мешаем
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) < new Version(7, 3, 4);
            }
            catch { return false; }
        }

        static bool? nvidia;
        public static bool Nvidia { get { if (nvidia == null) nvidia = HasNvidia(); return nvidia.Value; } }

        public static bool HasNvidia()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject m in q.Get())
                        if (((m["Name"] as string) ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { return true; } // не смогли проверить — не мешаем
            return false;
        }

        // ---------- список игр ----------
        public List<Game> ListGames()
        {
            var games = new List<Game>();
            // каждый источник сам по себе: нет лаунчера, битый файл, нет реестра — остальные всё равно читаются
            AddFrom(games, "Steam", SteamGames);
            AddFrom(games, "Epic", () => EpicGames(EpicManifestsDir));
            AddFrom(games, "GOG", () => GogGames(GogSource()));
            List<Dictionary<string, string>> uninstall = null;
            try { uninstall = UninstallSource(); } catch (Exception ex) { Trace("Uninstall: " + ex.Message); }
            if (uninstall != null)
            {
                AddFrom(games, "EA/Battle.net/Riot", () => UninstallGames(uninstall));
                AddFrom(games, "Ubisoft", () => UbiGames(UbiSource(), uninstall));
            }
            else AddFrom(games, "Ubisoft", () => UbiGames(UbiSource(), new List<Dictionary<string, string>>()));
            foreach (var c in St.Custom)
                games.Add(new Game { Key = "custom:" + c, Name = Path.GetFileNameWithoutExtension(c), Exes = { Path.GetFileName(c) } });
            // включённая игра пропала (удалили, перенесли) — всё равно показываем, чтобы её можно было выключить
            foreach (var kv in St.OnExes)
                if (!games.Any(g => g.Key == kv.Key))
                    games.Add(new Game { Key = kv.Key, AppId = Regex.IsMatch(kv.Key, @"^\d+$") ? kv.Key : null, Store = "не найдена",
                                         Name = kv.Key == "730" ? "Counter-Strike 2" : Path.GetFileNameWithoutExtension(kv.Value.Split(';')[0]),
                                         Exes = kv.Value.Split(';').ToList() });
            // включённые — наверх, дальше по алфавиту
            return games.OrderBy(g => St.On.ContainsKey(g.Key) ? 0 : 1).ThenBy(g => g.Name).ToList();
        }

        // Одна и та же игра из одного источника — одной строкой (ключ уникален внутри источника)
        void AddFrom(List<Game> games, string what, Func<List<Game>> src)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = src();
                if (list == null) return;
                foreach (var g in list)
                    if (g != null && g.Exes.Count > 0 && seen.Add(g.Key)) games.Add(g);
            }
            catch (Exception ex) { Trace(what + ": " + ex.Message); }
        }

        List<Game> SteamGames()
        {
            var games = new List<Game>();
            if (SteamDir != null && Directory.Exists(SteamDir))
            {
                var libs = new List<string> { SteamDir };
                var lf = Path.Combine(SteamDir, @"steamapps\libraryfolders.vdf");
                if (File.Exists(lf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(lf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (!libs.Any(x => string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libs.Add(p);
                    }
                foreach (var lib in libs)
                {
                    var sa = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(sa)) continue;
                    foreach (var acf in Directory.GetFiles(sa, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var t = File.ReadAllText(acf);
                            var id = Rx(t, "appid"); var name = Rx(t, "name"); var dir = Rx(t, "installdir");
                            if (id == null || name == null || NotGames.Contains(id) || NotGameName.IsMatch(name)) continue;
                            var g = new Game { Key = id, AppId = id, Name = name };
                            string[] known;
                            if (KnownExes.TryGetValue(id, out known)) g.Exes.AddRange(known);
                            else if (dir != null) { var e = GuessExe(Path.Combine(sa, "common", dir)); if (e != null) g.Exes.Add(e); }
                            if (g.Exes.Count > 0) games.Add(g);
                        }
                        catch (Exception ex) { Trace("Steam " + Path.GetFileName(acf) + ": " + ex.Message); }
                    }
                }
            }
            return games;
        }

        // ---------- другие лаунчеры (только чтение файлов и реестра, без сети и запуска) ----------

        // Epic: манифесты *.item — JSON с DisplayName, InstallLocation, LaunchExecutable, AppCategories
        public static List<Game> EpicGames(string dir)
        {
            var r = new List<Game>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return r;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    var t = File.ReadAllText(f);
                    string app = JStr(t, "AppName"), name = JStr(t, "DisplayName"), loc = JStr(t, "InstallLocation"),
                           exe = JStr(t, "LaunchExecutable"), main = JStr(t, "MainGameAppName");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;
                    if (JBool(t, "bIsIncompleteInstall") || NotGameName.IsMatch(name)) continue;
                    var cats = JArr(t, "AppCategories");
                    // у игр есть категория games; у движка, плагинов и ассетов — нет
                    if (cats != null && cats.Count > 0 && !cats.Contains("games", StringComparer.OrdinalIgnoreCase)) continue;
                    // DLC: ссылается на другую основную игру
                    if (!string.IsNullOrEmpty(main) && app != null && !string.Equals(main, app, StringComparison.OrdinalIgnoreCase)) continue;
                    string id = app ?? JStr(t, "CatalogItemId") ?? Path.GetFileNameWithoutExtension(f);
                    var g = new Game { Key = "epic:" + id, Name = name.Trim(), Store = "Epic" };
                    g.Exes.AddRange(PickExes(loc.Replace('/', '\\'), exe));
                    if (g.Exes.Count > 0) r.Add(g);
                }
                catch { }
            }
            return r;
        }

        // GOG Galaxy и офлайн-установщики GOG: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id>
        public static List<Game> GogGames(List<Dictionary<string, string>> entries)
        {
            var r = new List<Game>();
            foreach (var e in entries)
            {
                try
                {
                    string id = V(e, "gameID") ?? V(e, "_key"), name = V(e, "gameName"), dir = V(e, "path"), exe = V(e, "exe");
                    if (id == null || name == null || dir == null || NotGameName.IsMatch(name)) continue;
                    if (V(e, "dependsOn") != null) continue; // DLC
                    if (exe == null && V(e, "exeFile") != null) exe = Path.Combine(dir, V(e, "exeFile"));
                    var g = new Game { Key = "gog:" + id, Name = name, Store = "GOG" };
                    g.Exes.AddRange(PickExes(dir, exe));
                    if (g.Exes.Count > 0) r.Add(g);
                }
                catch { }
            }
            return r;
        }

        // Riot: у игр свой исполняемый файл не там, где обёртка
        static readonly Dictionary<string, string[]> RiotExes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
            { "valorant", new[] { "VALORANT-Win64-Shipping.exe" } }, { "league_of_legends", new[] { "League of Legends.exe" } } };

        // EA app, Battle.net и Riot пишут игры в «Программы и компоненты» с узнаваемой строкой удаления
        public static List<Game> UninstallGames(List<Dictionary<string, string>> entries)
        {
            var r = new List<Game>();
            foreach (var e in entries)
            {
                try
                {
                    string name = V(e, "DisplayName"), dir = V(e, "InstallLocation"), un = V(e, "UninstallString") ?? "";
                    if (name == null || dir == null || NotGameName.IsMatch(name)) continue;
                    dir = dir.Trim('"').Replace('/', '\\');
                    string store, key; string[] known = null;
                    if (un.IndexOf("EAInstaller", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // ...\Common Files\EAInstaller\<игра>\Cleanup.exe — так EA app (и Origin) ставят игры
                        if (name.StartsWith("EA app", StringComparison.OrdinalIgnoreCase)) continue;
                        store = "EA"; key = "ea:" + (V(e, "_key") ?? name);
                    }
                    else if (un.IndexOf("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string uid = Arg(un, "uid");
                        if (uid == null || uid.StartsWith("battle.net", StringComparison.OrdinalIgnoreCase)) continue; // сам Battle.net
                        store = "Battle.net"; key = "bnet:" + uid;
                    }
                    else if (un.IndexOf("RiotClientServices", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string prod = Arg(un, "uninstall-product");
                        if (prod == null || prod.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
                        store = "Riot"; key = "riot:" + prod + "." + (Arg(un, "uninstall-patchline") ?? "live");
                        RiotExes.TryGetValue(prod, out known);
                    }
                    else continue;
                    if (!Directory.Exists(dir)) continue;
                    var g = new Game { Key = key, Name = name, Store = store };
                    if (known != null) g.Exes.AddRange(known); else g.Exes.AddRange(PickExes(dir, null));
                    if (g.Exes.Count > 0) r.Add(g);
                }
                catch { }
            }
            return r;
        }

        // Ubisoft Connect: папки в HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>,
        // названия — в записях удаления «Uplay Install <id>»
        public static List<Game> UbiGames(List<Dictionary<string, string>> installs, List<Dictionary<string, string>> uninstall)
        {
            var r = new List<Game>();
            var names = new Dictionary<string, string>();
            var dirs = new Dictionary<string, string>();
            foreach (var e in uninstall)
            {
                var m = Regex.Match(V(e, "_key") ?? "", @"^Uplay Install (\d+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string id = m.Groups[1].Value;
                if (V(e, "DisplayName") != null) names[id] = V(e, "DisplayName");
                if (V(e, "InstallLocation") != null && !dirs.ContainsKey(id)) dirs[id] = V(e, "InstallLocation");
            }
            foreach (var e in installs)
            {
                string id = V(e, "_key"), d = V(e, "InstallDir");
                if (id != null && d != null) dirs[id] = d;
            }
            foreach (var kv in dirs)
            {
                try
                {
                    string dir = kv.Value.Trim('"').Replace('/', '\\').TrimEnd('\\');
                    string name;
                    if (!names.TryGetValue(kv.Key, out name)) name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || NotGameName.IsMatch(name)) continue;
                    var g = new Game { Key = "ubi:" + kv.Key, Name = name, Store = "Ubisoft" };
                    g.Exes.AddRange(PickExes(dir, null));
                    if (g.Exes.Count > 0) r.Add(g);
                }
                catch { }
            }
            return r;
        }

        // exe из лаунчера, если это не обёртка; иначе — догадка по папке игры.
        // У игр на Unreal exe лаунчера — заглушка, а кадры рисует *-Win64-Shipping.exe: берём оба.
        static List<string> PickExes(string dir, string exe)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return r;
            string main = null;
            if (!string.IsNullOrEmpty(exe))
            {
                string full = Path.IsPathRooted(exe) ? exe : Path.Combine(dir, exe);
                string n = Path.GetFileName(full);
                if (File.Exists(full) && !BadExe.IsMatch(n)) main = n;
            }
            string guess = GuessExe(dir);
            if (main != null) r.Add(main);
            if (guess != null && (main == null || (guess.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                                                   && !string.Equals(guess, main, StringComparison.OrdinalIgnoreCase)))) r.Add(guess);
            return r;
        }

        static string V(Dictionary<string, string> d, string k)
        {
            string v;
            return d != null && d.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        }

        // --name=значение или --name="значение с пробелами" из строки удаления
        static string Arg(string cmd, string name)
        {
            var m = Regex.Match(cmd, "--" + Regex.Escape(name) + "=(?:\"([^\"]*)\"|([^\\s\"]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        // Все подключи ключа реестра со значениями (только чтение); ошибки — пустой список
        static List<Dictionary<string, string>> ReadRegKeys(RegistryHive hive, RegistryView view, string path)
        {
            var r = new List<Dictionary<string, string>>();
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, view))
                using (var k = b.OpenSubKey(path))
                {
                    if (k == null) return r;
                    foreach (var n in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var s = k.OpenSubKey(n))
                            {
                                if (s == null) continue;
                                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                d["_key"] = n;
                                foreach (var v in s.GetValueNames())
                                {
                                    var o = s.GetValue(v);
                                    if (o is string || o is int) d[v] = o.ToString();
                                }
                                r.Add(d);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return r;
        }

        // Мини-чтение JSON манифестов Epic: строки, логические и массивы строк верхнего уровня
        static string JStr(string t, string key)
        {
            var m = Regex.Match(t, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? JUnesc(m.Groups[1].Value) : null;
        }

        static bool JBool(string t, string key)
        {
            return Regex.IsMatch(t, "\"" + Regex.Escape(key) + "\"\\s*:\\s*true", RegexOptions.IgnoreCase);
        }

        static List<string> JArr(string t, string key)
        {
            var m = Regex.Match(t, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            var r = new List<string>();
            foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"")) r.Add(JUnesc(s.Groups[1].Value));
            return r;
        }

        static string JUnesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                int code;
                if (n == 'n') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == 'r') sb.Append('\r');
                else if (n == 'b') sb.Append('\b');
                else if (n == 'f') sb.Append('\f');
                else if (n == 'u' && i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                { sb.Append((char)code); i += 4; }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        static string Rx(string t, string key)
        {
            var m = Regex.Match(t, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string GuessExe(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            var found = new List<FileInfo>();
            Walk(new DirectoryInfo(dir), 0, found);
            var good = found.Where(f => !BadExe.IsMatch(f.Name)).ToList();
            if (good.Count == 0) return null;
            var ship = good.FirstOrDefault(f => f.Name.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));
            if (ship != null) return ship.Name;
            return good.OrderByDescending(f => f.Length).First().Name;
        }

        static void Walk(DirectoryInfo d, int depth, List<FileInfo> acc)
        {
            if (depth > 5) return;
            try
            {
                acc.AddRange(d.GetFiles("*.exe"));
                foreach (var s in d.GetDirectories())
                    if (!Regex.IsMatch(s.Name, @"^(_CommonRedist|Redist|redist|DirectX|Support|Installer|Prerequisites|EasyAntiCheat|BattlEye|__Installer)$", RegexOptions.IgnoreCase))
                        Walk(s, depth + 1, acc);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        public int DefaultFps(Game g)
        {
            int v;
            if (St.Fps.TryGetValue(g.Key, out v)) return v;
            if (g.AppId == "730") return 240; // как в гайде
            // остальным — частота монитора: больше кадров экран не покажет, а запас до потолка
            // процессора держит 1% low у самого лимита (замер в Доте 30.10: при 90 не держала, при 60–65 — ровно)
            int hz = MonitorHz();
            if (hz > 60) hz -= 3;   // на частоте экрана с G-Sync/FreeSync FPS упирается в VSync — на 3 ниже (180 → 177)
            var cap = FpsCeilingFor(g);
            return cap.HasValue ? Math.Min(hz, cap.Value) : hz;
        }

        // Частота самого быстрого экрана; 59 Гц (59,94) считаем как 60
        public static int MonitorHz()
        {
            int hz = 0;
            try { foreach (Screen s in Screen.AllScreens) hz = Math.Max(hz, RefreshRate(s)); } catch { }
            if (hz % 10 == 9) hz++;
            return Math.Max(hz, 60);
        }

        // Задание автозапуска от старой версии (72 часа, не от батареи) пересоздаём
        public void RefreshAutostart()
        {
            if (TestMode || St.On.Count == 0) return;
            try { SetAutostart(true); } catch { }
            // Лимит держит только запущенный RivaTuner. Если его закрыли (или он упал) без перезагрузки,
            // автозапуск не сработает до следующего входа в Windows — поднимаем его через то же задание.
            if (Process.GetProcessesByName("RTSS").Length == 0 && File.Exists(Path.Combine(RtssDir ?? "", "RTSS.exe")))
                try { Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN \"" + TaskName + "\"") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        static int RefreshRate(Screen s)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return EnumDisplaySettings(s.DeviceName, -1, ref dm) ? dm.dmDisplayFrequency : 0;
        }

        // ---------- профиль RivaTuner ----------
        // reflex=true — лимитер через NVIDIA Reflex, как в гайде. Он только для CS2, где Reflex игры
        // выключен ключом -noreflex. В Доте (замер 30.10) он ронял FPS ниже лимита и раскачивал кадры
        // на 16–20 мс, поэтому остальным играм — обычный лимитер RivaTuner.
        // ssync=true — «Без разрывов»: Scanline Sync RivaTuner, кадр выводится, когда развёртка у самого низа экрана
        // (SyncScanline0=-100 — отсчёт от низа; стартовое значение с форума Blur Busters). Лимит тогда — частота экрана,
        // Limit=0. SyncTimeout=1 — медленный кадр не ждёт строку. 04.11 в Deadlock на 60 Гц: держит 60, полосы нет,
        // вслепую он выбрал его против обычного лимита 60.
        public static string Profile(int fps, bool reflex, bool ssync = false)
        {
            string r = reflex ? "1" : "0";
            if (ssync) { fps = 0; reflex = false; r = "0"; }
            return string.Join("\r\n", new[] {
                "[OSD]", "EnableOSD=1", "EnableBgnd=0", "EnableFill=0", "EnableStat=0", "BaseColor=FFFFFFFF", "BgndColor=00000000",
                "FillColor=80000000", "PositionX=1", "PositionY=3", "ZoomRatio=2", "CoordinateSpace=0", "EnableFrameColorBar=0",
                "FrameColorBarMode=0", "RefreshPeriod=500", "IntegerFramerate=1", "MaximumFrametime=1", "EnableFrametimeHistory=0",
                "FrametimeHistoryWidth=-32", "FrametimeHistoryHeight=-4", "FrametimeHistoryStyle=0", "ScaleToFit=0",
                "[Statistics]", "FramerateAveragingInterval=1000", "PeakFramerateCalc=0", "PercentileCalc=0", "FrametimeCalc=0", "PercentileBuffer=0",
                "[Framerate]", "Limit=" + fps, "LimitDenominator=1", "LimitTime=0", "LimitTimeDenominator=1", "PassiveWait=1", "SyncTimeout=" + (ssync ? "1" : "0"),
                "SyncScanline0=" + (ssync ? "-100" : "0"), "SyncScanline1=0", "SyncPeriods=0", "SyncFlush=1", "SyncLimiter=" + (reflex ? "3" : "0"), "SyncDisplay=0", "ReflexSleep=" + r,
                "ReflexSetLatencyMarker=1", "ReflexFallback=" + r, "ReflexSetSleepModePerFrame=" + r, "ReflexLowLatency=" + r,
                "ReflexLowLatencyBoost=" + r, "ReflexGetLatency=" + r,
                "[Hooking]", "EnableHooking=1", "EnableFloatingInjectionAddress=1", "EnableDynamicOffsetDetection=0", "HookLoadLibrary=0",
                "HookDirectDraw=1", "HookDirect3D8=1", "HookDirect3D9=1", "HookDirect3DSwapChain9Present=1", "HookDXGI=1",
                "HookDirect3D12=1", "HookOpenGL=1", "HookVulkan=1", "InjectionDelay=15000", "UseDetours=0",
                "[Font]", "Height=-9", "Weight=400", "Face=Unispace", "Load=",
                "[RendererDirect3D8]", "Implementation=2", "[RendererDirect3D9]", "Implementation=2", "[RendererDirect3D10]", "Implementation=2",
                "[RendererDirect3D11]", "Implementation=2", "[RendererDirect3D12]", "Implementation=2", "[RendererOpenGL]", "Implementation=2",
                "[RendererVulkan]", "Implementation=2", "" });
        }

        static readonly string[] RtssProcs = { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", "EncoderServer", "EncoderServer64" };

        // Сначала просим RivaTuner закрыться самому (WM_CLOSE его окнам): тогда он снимает хуки
        // из запущенных игр и сохраняет настройки. Kill — только если за 5 с не закрылся.
        bool StopRtss()
        {
            if (TestMode) return false;
            bool was = false;
            foreach (var p in Process.GetProcessesByName("RTSS"))
            {
                was = true;
                try
                {
                    uint pid = (uint)p.Id;
                    EnumWindows((h, l) => { uint wp; GetWindowThreadProcessId(h, out wp); if (wp == pid) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
                    if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch { }
            }
            // загрузчики хуков и кодировщик обычно уходят вместе с RTSS; оставшиеся — добить
            foreach (var n in RtssProcs.Skip(1))
                foreach (var p in Process.GetProcessesByName(n))
                    try { if (!p.WaitForExit(2000)) { p.Kill(); p.WaitForExit(5000); } } catch { }
            return was;
        }
        const uint WM_CLOSE = 0x0010;
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

        void StartRtss()
        {
            if (TestMode) return;
            Process.Start(new ProcessStartInfo(Path.Combine(RtssDir, "RTSS.exe")) { WorkingDirectory = RtssDir, UseShellExecute = true });
        }

        // ---------- применить профили на ходу ----------
        // RTSSHooks64.dll из SDK RivaTuner: UpdateProfiles — «force all currently loaded 3D applications
        // to reload the profiles» (SDK\Samples\SharedMemory\RTSSSharedMemorySample\RTSSProfileInterface.h).
        // Так лимит меняется без перезапуска RivaTuner: тот под запущенной игрой 04.11 уронил Deadlock.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UpdateProfilesFn();

        bool Perechitat()
        {
            if (Process.GetProcessesByName("RTSS").Length == 0) return false;
            try
            {
                var h = LoadLibrary(Path.Combine(RtssDir, Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll"));
                if (h == IntPtr.Zero) return false;
                var f = GetProcAddress(h, "UpdateProfiles");
                if (f == IntPtr.Zero) return false;
                ((UpdateProfilesFn)Marshal.GetDelegateForFunctionPointer(f, typeof(UpdateProfilesFn)))();
                return true;
            }
            catch (Exception) { return false; }
        }

        // RivaTuner запущен — перечитать профили на ходу; не запущен — просто запустить.
        // Перезапуск — только запасной путь, и только когда игра закрыта.
        void Primenit(Game g)
        {
            if (TestMode) return;
            bool naHodu = Perechitat();
            Zhurnal.Pishi("профили: " + (naHodu ? "перечитаны на ходу (UpdateProfiles)" : "UpdateProfiles не вышел, RivaTuner " + (Process.GetProcessesByName("RTSS").Length == 0 ? "не запущен — запускаю" : "перезапускаю")));
            if (naHodu) return;
            if (Process.GetProcessesByName("RTSS").Length == 0) { StartRtss(); return; }
            if (g != null && Running(g)) throw new Exception("RivaTuner не принял новый профиль на ходу. Закрой игру и нажми ещё раз.");
            StopRtss(); StartRtss();
        }

        const string TaskName = "RovnyFPS - RivaTuner";

        // Тот, кто сидит за компьютером (владелец explorer.exe). Через UAC с паролем другого админа
        // программа работает от чужого имени, а RivaTuner должен стартовать у игрока.
        static string InteractiveUser()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"))
                    foreach (ManagementObject m in q.Get())
                    {
                        var o = new object[2];
                        if (Convert.ToInt32(m.InvokeMethod("GetOwner", o)) == 0 && o[1] != null) return o[1] + "\\" + o[0];
                    }
            }
            catch { }
            return WindowsIdentity.GetCurrent().Name;
        }

        public static bool RunningAsOtherUser()
        {
            return !string.Equals(InteractiveUser(), WindowsIdentity.GetCurrent().Name, StringComparison.OrdinalIgnoreCase);
        }

        // Задание из XML: schtasks /SC ONLOGON сам ставит «не запускать от батареи» и «убить через 72 часа»
        public static string TaskXml(string rtssDir, string user)
        {
            Func<string, string> x = System.Security.SecurityElement.Escape;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + x(user) + "</UserId></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + x(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowStartOnDemand>true</AllowStartOnDemand>" +
                "<Enabled>true</Enabled><Priority>7</Priority></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + x(Path.Combine(rtssDir, "RTSS.exe")) + "</Command><WorkingDirectory>" + x(rtssDir) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        void SetAutostart(bool on)
        {
            if (TestMode) return;
            string xml = null, args;
            if (on)
            {
                xml = Path.Combine(Path.GetTempPath(), "rovnyfps-task.xml");
                File.WriteAllText(xml, TaskXml(RtssDir, InteractiveUser()), Encoding.Unicode);
                args = "/Create /TN \"" + TaskName + "\" /XML \"" + xml + "\" /F";
            }
            else args = "/Delete /TN \"" + TaskName + "\" /F";
            var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = oem, StandardErrorEncoding = oem });
            string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (xml != null) try { File.Delete(xml); } catch { }
            if (on && p.ExitCode != 0) throw new Exception("Не удалось включить автозапуск RivaTuner: " + err.Trim());
        }

        // ---------- параметры запуска Steam (только CS2) ----------
        // Файлы настроек аккаунтов Steam. При включении — только аккаунт, который сейчас вошёл
        // (не трогаем чужие аккаунты семьи); если вход неизвестен — все. При выключении — те, что мы меняли.
        public IEnumerable<string> LocalConfigs()
        {
            if (string.IsNullOrEmpty(SteamDir)) yield break;
            var ud = Path.Combine(SteamDir, "userdata");
            if (!Directory.Exists(ud)) yield break;
            foreach (var d in Directory.GetDirectories(ud))
            {
                if (Path.GetFileName(d) == "0") continue;
                var f = Path.Combine(d, @"config\localconfig.vdf");
                if (File.Exists(f)) yield return f;
            }
        }

        string ActiveAccount()
        {
            try
            {
                var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                var v = k != null ? k.GetValue("ActiveUser") : null;
                return v != null && Convert.ToInt64(v) != 0 ? Convert.ToInt64(v).ToString() : null;
            }
            catch { return null; }
        }

        static string Acc(string f) { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))); }

        static List<string> Tokens(string s) { return (s ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList(); }

        // Вынимает из строки наши ключи (вместе со значением) и возвращает вынутое
        static List<string> TakeOurs(List<string> toks)
        {
            var taken = new List<string>();
            foreach (var f in Cs2Flags)
            {
                int i;
                while ((i = toks.FindIndex(t => string.Equals(t, f[0], StringComparison.OrdinalIgnoreCase))) >= 0)
                {
                    taken.Add(toks[i]); toks.RemoveAt(i);
                    if (f.Length > 1 && i < toks.Count && !toks[i].StartsWith("-") && !toks[i].StartsWith("+")) { taken.Add(toks[i]); toks.RemoveAt(i); }
                }
            }
            return taken;
        }

        public static string MergeFlags(string cur)
        {
            var toks = Tokens(cur);
            TakeOurs(toks);
            foreach (var f in Cs2Flags) toks.AddRange(f);
            return string.Join(" ", toks);
        }

        // Откат: убрать наши ключи из ТЕКУЩЕЙ строки (то, что человек дописал сам, остаётся)
        // и вернуть те же ключи, если они были у него до нас (например его +fps_max 400)
        public static string Unmerge(string cur, string orig)
        {
            var toks = Tokens(cur);
            TakeOurs(toks);
            toks.AddRange(TakeOurs(Tokens(orig)));
            return string.Join(" ", toks);
        }

        static bool Changes(bool apply, string cur, string next)
        {
            return apply ? (cur == null || next != cur) : (cur != null && next != cur);
        }

        // apply=true — дописать флаги, false — убрать
        void EditLaunchOptions(string appId, bool apply)
        {
            var files = LocalConfigs().ToList();
            if (apply)
            {
                var active = ActiveAccount();
                if (active != null && files.Any(f => Acc(f) == active)) files = files.Where(f => Acc(f) == active).ToList();
            }
            else files = files.Where(f => St.Orig.ContainsKey(appId + "|" + Acc(f))).ToList();
            if (files.Count == 0) return;

            Func<string, string, string> nextFor = (f, cur) =>
            {
                if (apply) return MergeFlags(cur);
                string o = St.Orig[appId + "|" + Acc(f)], orig = "";
                if (o != "-") try { orig = Encoding.UTF8.GetString(Convert.FromBase64String(o)); } catch { }
                return Unmerge(cur, orig);
            };
            // нечего менять — Steam не трогаем (иначе «Обновить лимит» каждый раз перезапускал бы его)
            bool need = files.Any(f =>
            {
                var cur = Vdf.GetLaunchOptions(File.ReadAllText(f, new UTF8Encoding(false)), appId);
                return Changes(apply, cur, nextFor(f, cur));
            });
            if (!need)
            {
                if (!apply) { foreach (var f in files) St.Orig.Remove(appId + "|" + Acc(f)); St.Save(); }
                return;
            }
            if (!TestMode && Process.GetProcessesByName("cs2").Length > 0)
                throw new Exception("Закрой CS2 и нажми ещё раз — мне нужно перезапустить Steam.");

            bool steamWasRunning = CloseSteam();
            try
            {
                foreach (var f in files)
                {
                    string key = appId + "|" + Acc(f);
                    string text = File.ReadAllText(f, new UTF8Encoding(false)); // перечитываем: Steam мог переписать файл при выходе
                    string cur = Vdf.GetLaunchOptions(text, appId);
                    Trace(Acc(f) + ": в Steam сейчас «" + (cur ?? "<нет>") + "»");
                    if (apply && !St.Orig.ContainsKey(key))
                    {
                        St.Orig[key] = cur == null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(cur));
                        St.Save(); // исходник — на диск ДО правки файла Steam
                    }
                    string next = nextFor(f, cur);
                    if (Changes(apply, cur, next))
                    {
                        string bak = f + ".rovnyfps.bak";
                        if (!File.Exists(bak)) File.Copy(f, bak);
                        Disk.Write(f, Vdf.SetLaunchOptions(text, appId, next), new UTF8Encoding(false));
                    }
                    if (!apply) { St.Orig.Remove(key); St.Save(); }
                }
            }
            finally { if (steamWasRunning) StartSteamAsUser(); }
        }

        bool CloseSteam()
        {
            if (TestMode) return false;
            if (Process.GetProcessesByName("steam").Length == 0) return false;
            Process.Start(Path.Combine(SteamDir, "steam.exe"), "-shutdown");
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (Process.GetProcessesByName("steam").Length == 0) { Thread.Sleep(1500); return true; }
            }
            throw new Exception("Steam не закрылся. Закрой его сам и нажми ещё раз.");
        }

        void StartSteamAsUser()
        {
            // через explorer Steam запустится от обычного пользователя, а не от администратора
            Process.Start("explorer.exe", "\"" + Path.Combine(SteamDir, "steam.exe") + "\"");
        }

        // ---------- включить / выключить ----------
        public string Enable(Game g, int fps, Action<string> say)
        {
            if (!RtssInstalled)
            {
                say("Ставлю RivaTuner… около минуты");
                InstallRtss();
                say(g.AppId == "730" ? "Настраиваю… Steam перезапустится" : "Настраиваю…");
            }
            bool cs2 = g.AppId == "730";
            if (cs2 && RtssTooOldForReflex()) throw new Exception("RivaTuner слишком старый для CS2 — обнови MSI Afterburner/RivaTuner и нажми ещё раз.");
            // один профиль RivaTuner = одно имя exe; если оно уже занято другой игрой, лимиты перепутаются
            foreach (var kv in St.OnExes)
                if (kv.Key != g.Key)
                    foreach (var e in kv.Value.Split(';'))
                        if (g.Exes.Contains(e, StringComparer.OrdinalIgnoreCase))
                            throw new Exception(e + " уже настроен для другой игры — сначала выключи её.");

            Directory.CreateDirectory(ProfilesDir);
            try
            {
                // exe поменялся (игра обновилась) — профиль старого exe убираем
                string oldExes;
                if (St.OnExes.TryGetValue(g.Key, out oldExes))
                    foreach (var e in oldExes.Split(';'))
                        if (!g.Exes.Contains(e, StringComparer.OrdinalIgnoreCase)) RemoveProfile(e);
                foreach (var exe in g.Exes)
                {
                    var cfg = Path.Combine(ProfilesDir, exe + ".cfg");
                    var bak = cfg + ".rovnyfps.bak";
                    if (File.Exists(cfg) && !St.On.ContainsKey(g.Key) && !File.Exists(bak)) File.Copy(cfg, bak);
                    Disk.Write(cfg, Profile(fps, cs2, !cs2 && St.Ssync.Contains(g.Key)), Encoding.ASCII);
                }
                St.On[g.Key] = fps; St.OnExes[g.Key] = string.Join(";", g.Exes); St.Fps[g.Key] = fps;
                St.Save();
                SetAutostart(true);
                if (cs2) EditLaunchOptions("730", true);
                St.Save();
            }
            finally { Primenit(g); } // даже после ошибки RivaTuner не должен остаться выключенным
            // CS2 перезапускать всё равно: параметры запуска Steam читаются при старте игры
            string tail = cs2 ? "Запускай CS2." : "Если в игре есть свой лимит FPS — поставь его выше этого или выключи.";
            return (cs2 && Running(g) ? "Готово. Перезапусти CS2. " : "Готово. ") + tail;
        }

        void RemoveProfile(string exe)
        {
            var cfg = Path.Combine(ProfilesDir, exe + ".cfg");
            var bak = cfg + ".rovnyfps.bak";
            if (File.Exists(bak)) { File.Copy(bak, cfg, true); File.Delete(bak); }
            else if (File.Exists(cfg)) File.Delete(cfg);
        }

        // Профили, записанные старой версией с Reflex для всех игр, переписываем на обычный лимитер
        public void FixOldProfiles()
        {
            bool changed = false;
            try
            {
                foreach (var kv in St.On.ToList())
                {
                    if (kv.Key == "730") continue;
                    foreach (var exe in St.OnExes[kv.Key].Split(';'))
                    {
                        var cfg = Path.Combine(ProfilesDir, exe + ".cfg");
                        if (!File.Exists(cfg) || !File.ReadAllText(cfg).Contains("SyncLimiter=3")) continue;
                        changed = true;
                        Disk.Write(cfg, Profile(kv.Value, false, St.Ssync.Contains(kv.Key)), Encoding.ASCII);
                    }
                }
            }
            finally { if (changed) try { Primenit(null); } catch (Exception) { } }
        }

        public string Disable(Game g)
        {
            try
            {
                string exes;
                var list = St.OnExes.TryGetValue(g.Key, out exes) ? exes.Split(';') : g.Exes.ToArray();
                foreach (var exe in list) RemoveProfile(exe);
                St.On.Remove(g.Key); St.OnExes.Remove(g.Key);
                St.Save();
                if (g.AppId == "730") EditLaunchOptions("730", false);
                St.Save();
                if (St.On.Count == 0) SetAutostart(false);
            }
            finally { if (Process.GetProcessesByName("RTSS").Length > 0 || St.On.Count > 0) Primenit(g); }
            return St.On.Count == 0
                ? "Выключено. Лимитов больше нет. RivaTuner остался — удалить его можно в «Приложениях» Windows."
                : "Выключено. Лимита для этой игры больше нет.";
        }

        // Перезапуск RivaTuner под запущенной игрой 04.11 уронил Deadlock (1 раз из 6), поэтому
        // профиль меняем только при закрытой игре.
        public bool Running(Game g)
        {
            return !TestMode && g.Exes.Any(e => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e)).Length > 0);
        }

        // ---------- подбор лимита: временный профиль без лимита на время замера ----------
        // Свой профиль игрока сохраняем так же, как Enable, чтобы VernutProfile вернул всё как было.
        public void ZamerProfile(Game g)
        {
            Directory.CreateDirectory(ProfilesDir);
            try
            {
                foreach (var exe in g.Exes)
                {
                    var cfg = Path.Combine(ProfilesDir, exe + ".cfg");
                    var bak = cfg + ".rovnyfps.bak";
                    if (File.Exists(cfg) && !St.On.ContainsKey(g.Key) && !File.Exists(bak)) File.Copy(cfg, bak);
                    Disk.Write(cfg, Profile(0, false), Encoding.ASCII);
                }
            }
            finally { Primenit(g); }
        }

        public void VernutProfile(Game g, bool restart)
        {
            try
            {
                foreach (var exe in g.Exes)
                {
                    if (St.On.ContainsKey(g.Key)) Disk.Write(Path.Combine(ProfilesDir, exe + ".cfg"), Profile(St.On[g.Key], g.AppId == "730", g.AppId != "730" && St.Ssync.Contains(g.Key)), Encoding.ASCII);
                    else RemoveProfile(exe);
                }
            }
            finally { if (restart) Primenit(g); }
        }
    }

    // ---------------- Окно ----------------
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(14, 14, 16), Card = Color.FromArgb(24, 24, 27), CardHover = Color.FromArgb(32, 32, 36),
            CardSel = Color.FromArgb(39, 39, 44), Border = Color.FromArgb(58, 58, 64), BorderSel = Color.FromArgb(96, 96, 104),
            Txt = Color.FromArgb(244, 244, 245), Muted = Color.FromArgb(150, 150, 158), Faint = Color.FromArgb(82, 82, 91),
            Green = Color.FromArgb(74, 222, 128), Amber = Color.FromArgb(251, 191, 36), Red = Color.FromArgb(248, 113, 113);
        public static float K = 1f;
        public static int S(float v) { return (int)Math.Round(v * K); }
        public static readonly Font Title = new Font("Segoe UI Semibold", 15.5f), Body = new Font("Segoe UI", 10f),
            Name = new Font("Segoe UI Semibold", 10.5f), Small = new Font("Segoe UI", 8.75f), SmallSemi = new Font("Segoe UI Semibold", 8.75f),
            Value = new Font("Segoe UI Semibold", 17f), Button = new Font("Segoe UI Semibold", 10.5f), Glyph = new Font("Segoe UI", 14f);

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
    }

    // Кнопка своей отрисовки: основная (белая), второстепенная (тёмная с рамкой), чип и круглая «−/+»
    class Btn : Control
    {
        public enum Kind { Primary, Secondary, Chip, Round }
        readonly Kind kind;
        public bool Selected;
        bool hover, down;

        public Btn(Kind k)
        {
            kind = k;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true; BackColor = Ui.Bg;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            Color fill, text, border = Color.Empty;
            switch (kind)
            {
                case Kind.Primary:
                    fill = !Enabled ? Ui.CardSel : down ? Color.FromArgb(212, 212, 216) : hover ? Color.White : Ui.Txt;
                    text = Enabled ? Ui.Bg : Ui.Muted; break;
                case Kind.Chip:
                    fill = Selected ? Ui.Txt : down ? Ui.CardSel : hover ? Ui.CardHover : Ui.Card;
                    text = Selected ? Ui.Bg : hover ? Ui.Txt : Ui.Muted; border = Selected ? Color.Empty : Ui.Border; break;
                default:
                    fill = down ? Ui.CardSel : hover ? Ui.CardHover : Ui.Card;
                    text = Enabled ? Ui.Txt : Ui.Faint; border = hover ? Ui.BorderSel : Ui.Border; break;
            }
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = kind == Kind.Chip || kind == Kind.Round ? Height / 2f : Ui.S(11);
            Ui.Fill(g, r, rad, fill, border);
            if (Focused && ShowFocusCues) using (var p = Ui.Round(new RectangleF(2, 2, Width - 5, Height - 5), rad - 2)) using (var pen = new Pen(Color.FromArgb(120, Ui.Green))) g.DrawPath(pen, p);
            var f = kind == Kind.Round ? Ui.Glyph : kind == Kind.Chip ? Ui.SmallSemi : Ui.Button;
            TextRenderer.DrawText(g, Text, f, new Rectangle(0, kind == Kind.Round ? -Ui.S(1) : 0, Width, Height), text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // Список игр: карточки, подсветка под мышкой, тонкая тёмная прокрутка, клавиши вверх/вниз
    class GameList : Control
    {
        public List<Game> Items = new List<Game>();
        public Func<Game, int?> OnFps = delegate { return null; };
        public Func<Game, bool> Warn = delegate { return false; };
        public string Empty = "";
        public event EventHandler SelectedChanged;
        int sel = -1, hover = -1, scroll;

        public GameList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; BackColor = Ui.Bg; AccessibleRole = AccessibleRole.List; AccessibleName = "Игры";
        }

        int RowH { get { return Ui.S(54); } }
        int Gap { get { return Ui.S(6); } }
        int Content { get { return Items.Count * (RowH + Gap) - Gap; } }
        int MaxScroll { get { return Math.Max(0, Content - Height); } }
        void Clamp() { scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); }

        public Game Selected { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }
        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                sel = value;
                if (sel >= 0) { int top = sel * (RowH + Gap); if (top < scroll) scroll = top; else if (top + RowH > scroll + Height) scroll = top + RowH - Height; Clamp(); }
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        public void SetItems(List<Game> items, int select) { Items = items; scroll = 0; hover = -1; SelectedIndex = select; }

        int At(Point p)
        {
            int y = p.Y + scroll, i = y / (RowH + Gap);
            return i < 0 || i >= Items.Count || y - i * (RowH + Gap) > RowH ? -1 : i;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.Location); if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); int i = At(e.Location); if (i >= 0 && i != sel) SelectedIndex = i; base.OnMouseDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { scroll -= e.Delta * RowH / 120; Clamp(); hover = At(e.Location); Invalidate(); base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && sel < Items.Count - 1) SelectedIndex = sel + 1;
            if (e.KeyCode == Keys.Up && sel > 0) SelectedIndex = sel - 1;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Items.Count == 0)
            {
                Ui.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(12), Ui.Bg, Ui.Border);
                TextRenderer.DrawText(g, Empty, Ui.Body, ClientRectangle, Ui.Muted, Ui.Bg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int w = Width - (MaxScroll > 0 ? Ui.S(12) : 0);
            for (int i = 0; i < Items.Count; i++)
            {
                int y = i * (RowH + Gap) - scroll;
                if (y + RowH < 0 || y > Height) continue;
                var game = Items[i];
                bool isSel = i == sel, isHover = i == hover;
                var r = new RectangleF(0.5f, y + 0.5f, w - 1.5f, RowH - 1);
                Ui.Fill(g, r, Ui.S(12), isSel ? Ui.CardSel : isHover ? Ui.CardHover : Ui.Card, isSel ? Ui.BorderSel : Color.Empty);

                int right = w - Ui.S(14);
                var fps = OnFps(game);
                if (fps.HasValue)
                {
                    string t = fps.Value + " FPS";
                    var sz = TextRenderer.MeasureText(t, Ui.SmallSemi);
                    int pw = sz.Width + Ui.S(30), ph = Ui.S(24), px = right - pw, py = y + (RowH - ph) / 2;
                    Ui.Fill(g, new RectangleF(px, py, pw, ph), ph / 2f, Color.FromArgb(30, Ui.Green), Color.Empty);
                    int dot = Ui.S(7);
                    using (var b = new SolidBrush(Ui.Green)) g.FillEllipse(b, px + Ui.S(9), py + (ph - dot) / 2f, dot, dot);
                    TextRenderer.DrawText(g, t, Ui.SmallSemi, new Rectangle(px + Ui.S(22), py, pw - Ui.S(24), ph), Ui.Green,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    right = px - Ui.S(8);
                }
                else if (Warn(game))
                {
                    int dot = Ui.S(7);
                    using (var b = new SolidBrush(Ui.Amber)) g.FillEllipse(b, right - dot, y + (RowH - dot) / 2f, dot, dot);
                    right -= dot + Ui.S(10);
                }
                var bg = isSel ? Ui.CardSel : isHover ? Ui.CardHover : Ui.Card;
                TextRenderer.DrawText(g, game.Name, Ui.Name, new Rectangle(Ui.S(16), y + Ui.S(8), right - Ui.S(16), Ui.S(22)), Ui.Txt, bg,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                string sub = (game.Store != null ? game.Store + " · " : "") + string.Join(", ", game.Exes);
                TextRenderer.DrawText(g, sub, Ui.Small, new Rectangle(Ui.S(16), y + Ui.S(30), right - Ui.S(16), Ui.S(18)), Ui.Muted, bg,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
            if (MaxScroll > 0)
            {
                float th = Math.Max(Ui.S(28), (float)Height * Height / Content), ty = scroll * (Height - th) / MaxScroll;
                Ui.Fill(g, new RectangleF(Width - Ui.S(5), ty, Ui.S(4), th), Ui.S(2), Ui.Faint, Color.Empty);
            }
        }
    }

    // Строка состояния: цветная точка + текст в две строки
    class StatusLine : Control
    {
        public enum Tone { Info, Ok, Warn, Error }
        Tone tone;
        public StatusLine() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Ui.Bg; }
        public void Show(string text, Tone t) { Text = text ?? ""; tone = t; AccessibleName = Text; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Text == "") return;
            Color c = tone == Tone.Ok ? Ui.Green : tone == Tone.Warn ? Ui.Amber : tone == Tone.Error ? Ui.Red : Ui.Muted;
            int x = 0;
            if (tone != Tone.Info)
            {
                int dot = Ui.S(7);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 0, Ui.S(6), dot, dot);
                x = dot + Ui.S(8);
            }
            TextRenderer.DrawText(g, Text, Ui.Small, new Rectangle(x, 0, Width - x, Height), tone == Tone.Info ? Ui.Muted : (tone == Tone.Ok ? Ui.Txt : c), Ui.Bg,
                TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
    }

    class MainForm : Form
    {
        readonly Core core;
        List<Game> games = new List<Game>();
        readonly GameList list = new GameList();
        readonly Btn main = new Btn(Btn.Kind.Primary), off = new Btn(Btn.Kind.Secondary), minus = new Btn(Btn.Kind.Round), plus = new Btn(Btn.Kind.Round);
        readonly List<Btn> chips = new List<Btn>();
        readonly Btn ssync = new Btn(Btn.Kind.Chip) { Text = "Без разрывов" };   // Scanline Sync, справа в ряду чисел
        readonly Label value = new Label(), hint = new Label(), sub = new Label(), section = new Label();
        readonly LinkLabel other = new LinkLabel(), support = new LinkLabel(), podbor = new LinkLabel(), proverka = new LinkLabel(), zhurnal = new LinkLabel();
        bool pmOtkaz;   // отказался качать PresentMon или он не скачался — мерим буфером RivaTuner
        CancellationTokenSource podborStop;   // не null — идёт подбор лимита, главная кнопка его отменяет
        readonly StatusLine status = new StatusLine();
        readonly Btn upd = new Btn(Btn.Kind.Chip);   // «Обновить до версии N» — видна, только когда есть новая (Obnovlenie.cs)
        Obnovlenie.Est novoe;
        readonly System.Windows.Forms.Timer repeat = new System.Windows.Forms.Timer();
        Image logo;
        int fps = 60, hz = 60, step, held;
        bool busy, otherUser;
        public bool Loaded;

        public MainForm(Core c)
        {
            core = c;
            using (var gr = CreateGraphics()) Ui.K = gr.DpiX / 96f;
            Func<float, int> S = v => Ui.S(v);
            Text = "Ровный FPS";
            Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Txt;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(420), S(640));
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { var st = typeof(MainForm).Assembly.GetManifestResourceStream("logo64.png"); if (st != null) logo = Image.FromStream(st); } catch { }
            hz = Core.MonitorHz();

            sub.Text = "Выбери игру — RivaTuner удержит ровный FPS";
            sub.ForeColor = Ui.Muted; sub.Font = Ui.Small; sub.SetBounds(S(66), S(47), S(340), S(20));

            list.SetBounds(S(22), S(86), S(376), S(294));
            list.Empty = "Ищу игры…";
            list.OnFps = g => core.St.On.ContainsKey(g.Key) ? (int?)core.St.On[g.Key] : null;
            list.Warn = g => Core.WarningFor(g) != null;
            list.SelectedChanged += delegate { Refresh2(); };

            other.Text = "+ Другая игра…"; other.Font = Ui.Small; other.AutoSize = true; other.Location = new Point(S(22), S(390));
            other.LinkColor = Ui.Muted; other.ActiveLinkColor = Ui.Txt; other.VisitedLinkColor = Ui.Muted;
            other.LinkBehavior = LinkBehavior.HoverUnderline; other.Cursor = Cursors.Hand;
            other.LinkClicked += delegate { AddCustom(); };

            // программа бесплатная; неброская ссылка на донаты справа в той же строке
            support.Text = "Поддержать"; support.Font = Ui.Small; support.AutoSize = true;
            support.LinkColor = Ui.Muted; support.ActiveLinkColor = Ui.Txt; support.VisitedLinkColor = Ui.Muted;
            support.LinkBehavior = LinkBehavior.HoverUnderline; support.Cursor = Cursors.Hand;
            support.AccessibleName = "Поддержать автора на Boosty";
            support.Location = new Point(S(398) - TextRenderer.MeasureText(support.Text, Ui.Small).Width, S(390));
            support.LinkClicked += delegate { try { Process.Start(Program.SupportUrl); } catch { } };

            // журнал шагов для разбора сбоев — показать файл в Проводнике, его можно переслать
            zhurnal.Text = "Журнал"; zhurnal.Font = Ui.Small; zhurnal.AutoSize = true;
            zhurnal.LinkColor = Ui.Muted; zhurnal.ActiveLinkColor = Ui.Txt; zhurnal.VisitedLinkColor = Ui.Muted;
            zhurnal.LinkBehavior = LinkBehavior.HoverUnderline; zhurnal.Cursor = Cursors.Hand;
            zhurnal.AccessibleName = "Показать журнал программы — его можно переслать автору";
            zhurnal.Location = new Point(support.Left - S(16) - TextRenderer.MeasureText(zhurnal.Text, Ui.Small).Width, S(390));
            zhurnal.LinkClicked += delegate
            {
                if (!File.Exists(Zhurnal.Put)) Zhurnal.Pishi("журнал открыт");
                try { Process.Start("explorer.exe", "/select,\"" + Zhurnal.Put + "\""); } catch { }
            };

            section.Text = "ЛИМИТ КАДРОВ"; section.Font = Ui.SmallSemi; section.ForeColor = Ui.Muted; section.AutoSize = true; section.Location = new Point(S(22), S(428));

            // замер игры без лимита → лимит чуть ниже её 1% low → замер с ним и строка «до/после»
            podbor.Text = "Подобрать по замеру"; podbor.Font = Ui.Small; podbor.AutoSize = true;
            podbor.LinkColor = Ui.Muted; podbor.ActiveLinkColor = Ui.Txt; podbor.VisitedLinkColor = Ui.Muted;
            podbor.LinkBehavior = LinkBehavior.HoverUnderline; podbor.Cursor = Cursors.Hand;
            podbor.AccessibleName = "Подобрать лимит по замеру игры";
            podbor.Location = new Point(S(398) - TextRenderer.MeasureText(podbor.Text, Ui.Small).Width, S(428));
            podbor.LinkClicked += delegate { Podobrat(); };

            // свой лимит: поставить число из поля и замерить, держит ли его компьютер
            proverka.Text = "Проверить"; proverka.Font = Ui.Small; proverka.AutoSize = true;
            proverka.LinkColor = Ui.Muted; proverka.ActiveLinkColor = Ui.Txt; proverka.VisitedLinkColor = Ui.Muted;
            proverka.LinkBehavior = LinkBehavior.HoverUnderline; proverka.Cursor = Cursors.Hand;
            proverka.AccessibleName = "Проверить замером выбранный лимит";
            proverka.Location = new Point(podbor.Left - S(16) - TextRenderer.MeasureText(proverka.Text, Ui.Small).Width, S(428));
            proverka.LinkClicked += delegate { Proverit(); };

            minus.Text = "−"; plus.Text = "+";
            minus.SetBounds(S(22), S(452), S(38), S(38)); plus.SetBounds(S(142), S(452), S(38), S(38));
            minus.AccessibleName = "Меньше"; plus.AccessibleName = "Больше";
            value.SetBounds(S(62), S(452), S(78), S(38)); value.Font = Ui.Value; value.TextAlign = ContentAlignment.MiddleCenter; value.ForeColor = Ui.Txt;
            value.Cursor = Cursors.SizeNS;
            value.MouseWheel += (s, e) => Nudge(e.Delta > 0 ? 5 : -5);
            value.MouseEnter += delegate { value.Focus(); };
            foreach (var b in new[] { minus, plus })
            {
                var d = b == plus ? 1 : -1;
                b.MouseDown += (s, e) => { if (e.Button != MouseButtons.Left) return; step = d; held = 0; Nudge(d * 5); repeat.Interval = 400; repeat.Start(); };
                b.MouseUp += delegate { repeat.Stop(); };
                b.MouseLeave += delegate { repeat.Stop(); };
                b.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Nudge(d * 5); };
            }
            repeat.Tick += delegate { held++; repeat.Interval = held > 6 ? 40 : 90; Nudge(step * (held > 12 ? 10 : 5)); };

            hint.Text = "Чуть ниже FPS, который игра держит в драках. Экран: " + hz + " Гц";
            hint.ForeColor = Ui.Muted; hint.Font = Ui.Small; hint.SetBounds(S(194), S(452), S(204), S(40));

            main.SetBounds(S(22), S(546), S(376), S(46));
            main.Click += delegate { if (podborStop != null) podborStop.Cancel(); else Run(true); };
            off.Text = "Выключить";
            off.Click += delegate { Run(false); };

            status.SetBounds(S(22), S(602), S(376), S(34));

            ssync.AccessibleName = "Без разрывов: подгонять кадры под экран (Scanline Sync)";
            int sw = TextRenderer.MeasureText(ssync.Text, Ui.SmallSemi).Width + S(28);
            ssync.SetBounds(S(398) - sw, S(502), sw, S(30));
            ssync.Click += delegate { PereklyuchitSsync(); };

            upd.Selected = true; upd.Visible = false;
            upd.Click += delegate { Obnovit(); };

            Controls.AddRange(new Control[] { sub, list, other, support, zhurnal, section, proverka, podbor, minus, value, plus, hint, main, off, status, upd, ssync });
            SetFps(60);
            UpdateButtons();

            // необязательное встраивание во вкладку оболочки (Vstraivanie.cs); значка в трее у нас нет
            Vstraivanie.Podklyuchit(Handle, null);
        }

        // Вторая копия просит первую показаться: если окно во вкладке оболочки, её откроет оболочка
        static readonly uint PokazhisMsg = RegisterWindowMessage("RovnyFPS.Pokazhis");
        protected override void WndProc(ref Message m)
        {
            if (PokazhisMsg != 0 && m.Msg == (int)PokazhisMsg) { m.Result = new IntPtr(Vstraivanie.Pokazat() ? 1 : 0); return; }
            base.WndProc(ref m);
        }

        // true — первая копия ответила, что её покажет оболочка
        public static bool AskFirstCopy()
        {
            int self = Process.GetCurrentProcess().Id;
            var pids = new HashSet<int>(Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Where(p => p.Id != self).Select(p => p.Id));
            bool shown = false;
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                var cls = new StringBuilder(256); GetClassName(h, cls, 256);
                if (!cls.ToString().StartsWith("WindowsForms10.Window")) return true;
                UIntPtr r;
                if (SendMessageTimeout(h, PokazhisMsg, IntPtr.Zero, IntPtr.Zero, 2, 3000, out r) != IntPtr.Zero && r.ToUInt64() == 1) { shown = true; return false; }
                return true;
            }, IntPtr.Zero);
            return shown;
        }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint ms, out UIntPtr r);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (logo != null) g.DrawImage(logo, Ui.S(22), Ui.S(20), Ui.S(34), Ui.S(34));
            TextRenderer.DrawText(g, "Ровный FPS", Ui.Title, new Point(Ui.S(64), Ui.S(17)), Ui.Txt, Ui.Bg);
            // номер версии справа вверху — на его месте появляется кнопка «Обновить»
            if (!upd.Visible)
            {
                string v = "версия " + Program.Versiya;
                int w = TextRenderer.MeasureText(v, Ui.Small).Width;
                TextRenderer.DrawText(g, v, Ui.Small, new Point(Ui.S(398) - w, Ui.S(27)), Ui.Muted, Ui.Bg);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!Loaded) Reload(null);
            ProveritObnovlenie();
        }

        async void ProveritObnovlenie()
        {
            var est = await Task.Run(() => Obnovlenie.Proverit(Program.ManifestUrl, Program.Versiya));
            if (est == null || IsDisposed) return;
            PokazatObnovlenie(est);
        }

        public void PokazatObnovlenie(Obnovlenie.Est est)
        {
            novoe = est;
            upd.Text = "Обновить";
            int w = TextRenderer.MeasureText(upd.Text, Ui.SmallSemi).Width + Ui.S(28);
            upd.SetBounds(Ui.S(398) - w, Ui.S(22), w, Ui.S(30));
            upd.AccessibleName = "Обновить до версии " + est.Versiya;
            upd.Visible = true;
            // подпись под заголовком короче, чтобы не уйти под кнопку
            sub.Text = "Вышла версия " + est.Versiya;
            sub.Width = upd.Left - sub.Left - Ui.S(8);
        }

        async void Obnovit()
        {
            if (novoe == null || busy) return;
            busy = true; upd.Enabled = main.Enabled = off.Enabled = list.Enabled = other.Enabled = false;
            status.Show("Скачиваю версию " + novoe.Versiya + "…", StatusLine.Tone.Info);
            try { await Task.Run(() => Obnovlenie.Skachat(novoe)); }
            catch (Exception ex)
            {
                busy = false; upd.Enabled = off.Enabled = list.Enabled = other.Enabled = true; Refresh2();
                status.Show(ex.Message, StatusLine.Tone.Error);
                return;
            }
            busy = false;
            Obnovlenie.Perezapustit();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // посреди работы закрывать нельзя: Steam останется закрытым, файл — недописанным
            if (podborStop != null) { podborStop.Cancel(); e.Cancel = true; return; }
            if (busy) { e.Cancel = true; status.Show("Подожди, заканчиваю…", StatusLine.Tone.Info); return; }
            base.OnFormClosing(e);
        }

        // Список игр ищется в фоне: окно появляется сразу, а обход папок и WMI его не держат
        public async void Reload(string selectKey)
        {
            list.Empty = "Ищу игры…"; list.SetItems(new List<Game>(), -1);
            core.LoadState();
            var found = await Task.Run(() => { otherUser = Core.RunningAsOtherUser(); var n = Core.Nvidia; return core.ListGames(); });
            Fill(found, selectKey);
        }

        public void LoadNow() { core.LoadState(); Fill(core.ListGames(), null); }
        public void SelectForShot(int i) { if (i < games.Count) list.SelectedIndex = i; }

        void Fill(List<Game> found, string selectKey)
        {
            games = found; Loaded = true;
            list.Empty = "Игр не нашёл.\nДобавь игру вручную — «+ Другая игра…»";
            int idx = selectKey == null ? -1 : games.FindIndex(g => g.Key == selectKey);
            if (idx < 0 && games.Count > 0) idx = 0;
            list.SetItems(games, idx);
            if (games.Count == 0) Refresh2();
        }

        void Nudge(int d) { SetFps(fps + d); BuildChips(); }

        void SetFps(int v)
        {
            var g = list.Selected;
            int max = g != null && Core.FpsCeilingFor(g).HasValue ? Core.FpsCeilingFor(g).Value : 1000;
            fps = Math.Max(30, Math.Min(max, v));
            value.Text = fps.ToString();
            foreach (var c in chips) c.Selected = (int)c.Tag == fps;
            foreach (var c in chips) c.Invalidate();
        }

        // Быстрый выбор: экран, 120, 144, 240 — без того, что выше потолка игры
        void BuildChips()
        {
            var g = list.Selected;
            int max = g != null && Core.FpsCeilingFor(g).HasValue ? Core.FpsCeilingFor(g).Value : 1000;
            var vals = new[] { hz > 60 ? hz - 3 : hz, 120, 144, 240 }.Where(v => v <= max && v >= 30).Distinct().OrderBy(v => v).ToList();
            if (chips.Count == vals.Count && chips.Select(c => (int)c.Tag).SequenceEqual(vals)) { SetFps(fps); return; }
            foreach (var c in chips) { Controls.Remove(c); c.Dispose(); }
            chips.Clear();
            int x = Ui.S(22);
            foreach (var v in vals)
            {
                var c = new Btn(Btn.Kind.Chip) { Text = v.ToString(), Tag = v };
                int w = TextRenderer.MeasureText(c.Text, Ui.SmallSemi).Width + Ui.S(28);
                c.SetBounds(x, Ui.S(502), w, Ui.S(30));
                c.AccessibleName = v + " кадров";
                int vv = v;
                c.Click += delegate { SetFps(vv); };
                Controls.Add(c); chips.Add(c);
                x += w + Ui.S(8);
            }
            SetFps(fps);
        }

        void UpdateButtons()
        {
            var g = list.Selected;
            bool on = g != null && core.St != null && core.St.On.ContainsKey(g.Key);
            main.Text = podborStop != null ? "Отменить замер" : busy ? "Настраиваю…" : on ? "Обновить лимит" : "Включить";
            off.Visible = on;
            main.Width = on ? Ui.S(258) : Ui.S(376);
            off.SetBounds(Ui.S(288), main.Top, Ui.S(110), main.Height);
        }

        void Refresh2()
        {
            var g = list.Selected;
            bool on = g != null && core.St.On.ContainsKey(g.Key);
            if (g != null) SetFps(on ? core.St.On[g.Key] : core.DefaultFps(g));
            BuildChips();
            UpdateButtons();
            // только CS2 настраивается через NVIDIA Reflex; обычный лимитер работает на любой видеокарте
            bool blocked = g != null && g.AppId == "730" && !on && !Core.Nvidia;
            main.Enabled = g != null && !busy && !blocked;
            bool ss = g != null && core.St.Ssync.Contains(g.Key) && g.AppId != "730";
            ssync.Selected = ss; ssync.Invalidate();
            ssync.Enabled = g != null && !busy && g.AppId != "730";   // в CS2 — режим из гайда (Reflex), с ним не совмещается
            minus.Enabled = plus.Enabled = g != null && !busy && !ss;
            foreach (var c in chips) c.Enabled = !busy && !ss;
            hint.Text = ss ? "Кадры под экран " + hz + " Гц, без полосы разрыва. Нужен запас FPS."
                           : "Чуть ниже FPS, который игра держит в драках. Экран: " + hz + " Гц";
            podbor.Enabled = proverka.Enabled = g != null && !busy && !blocked;
            if (busy) return;
            string warn = g != null ? Core.WarningFor(g) : null;
            if (g == null) status.Show(Loaded ? "" : "", StatusLine.Tone.Info);
            else if (blocked) status.Show("Для CS2 нужна видеокарта NVIDIA.", StatusLine.Tone.Error);
            else if (on && g.Store == "не найдена") status.Show("Игра не найдена на диске — можно только выключить.", StatusLine.Tone.Warn);
            else if (on && ss) status.Show("Включено без разрывов: RivaTuner выводит кадры под экран " + hz + " Гц.", StatusLine.Tone.Ok);
            else if (on) status.Show("Включено. RivaTuner держит " + core.St.On[g.Key] + " FPS.", StatusLine.Tone.Ok);
            // главное для нового человека — сказать заранее, что без RivaTuner не обойтись
            else if (!core.RtssInstalled) status.Show("Нужен бесплатный RivaTuner. Его нет — скачаю и поставлю сам, когда нажмёшь «Включить».", StatusLine.Tone.Warn);
            else if (warn != null) status.Show(warn, StatusLine.Tone.Warn);
            else if (otherUser) status.Show("Окно открыто от имени другого пользователя.", StatusLine.Tone.Warn);
            else status.Show("", StatusLine.Tone.Info);
        }

        void PereklyuchitSsync()
        {
            var g = list.Selected; if (g == null || busy || g.AppId == "730") return;
            if (!core.St.Ssync.Remove(g.Key)) core.St.Ssync.Add(g.Key);
            core.St.Save();
            if (core.St.On.ContainsKey(g.Key)) Run(true);   // включённой игре — сразу, на ходу
            else Refresh2();
        }

        void AddCustom()
        {
            using (var d = new OpenFileDialog { Filter = "Игра (*.exe)|*.exe", Title = "Выбери exe игры" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string path = d.FileName, real;
                // выбрали лаунчер — берём exe самой игры (лимит на лаунчер не подействует)
                if (Core.LauncherToGame.TryGetValue(Path.GetFileName(path), out real))
                {
                    string dir = Path.GetDirectoryName(path), up = Path.GetDirectoryName(dir), found = null;
                    try
                    {
                        found = Directory.GetFiles(dir, real).FirstOrDefault();
                        if (found == null && up != null) found = Directory.GetFiles(up, real, SearchOption.AllDirectories).FirstOrDefault();
                    }
                    catch (Exception) { }
                    path = found ?? Path.Combine(dir, real);
                }
                if (!core.St.Custom.Contains(path)) { core.St.Custom.Add(path); core.St.Save(); }
                Reload("custom:" + path);
            }
        }

        async void Run(bool enable)
        {
            var g = list.Selected; if (g == null || busy) return;
            // качать чужую программу — только с согласия
            if (enable && !core.RtssInstalled && MessageBox.Show(this,
                    "Для лимита нужен RivaTuner Statistics Server — бесплатная программа от Guru3D, её ставят вместе с MSI Afterburner.\n\n" +
                    "Сейчас я скачаю и поставлю его сам, это около минуты. Потом он будет запускаться вместе с Windows и держать лимит.\n\n" +
                    "Поставить RivaTuner?", "Ровный FPS", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;
            int v = fps;
            busy = true; main.Enabled = off.Enabled = minus.Enabled = plus.Enabled = list.Enabled = other.Enabled = false;
            foreach (var c in chips) c.Enabled = false;
            UpdateButtons();
            status.Show(g.AppId == "730" ? "Настраиваю… Steam перезапустится." : "Настраиваю…", StatusLine.Tone.Info);
            string msg; bool ok = true;
            Action<string> say = t => BeginInvoke((Action)(() => status.Show(t, StatusLine.Tone.Info)));
            try { msg = await Task.Run(() => enable ? core.Enable(g, v, say) : core.Disable(g)); }
            catch (Exception ex) { msg = ex.Message; ok = false; }
            busy = false; off.Enabled = list.Enabled = other.Enabled = true;
            foreach (var c in chips) c.Enabled = true;
            core.LoadState();
            var found = await Task.Run(() => core.ListGames());
            Fill(found, g.Key);
            status.Show(msg, ok ? StatusLine.Tone.Ok : StatusLine.Tone.Error);
        }

        // ---------- «Подобрать по замеру» ----------
        // Один запуск игры: замер без лимита → ставим лимит на ходу (UpdateProfiles) → замер с ним.
        // Отмена возвращает профиль, каким он был до подбора.
        async void Podobrat()
        {
            var g = list.Selected; if (g == null || busy) return;
            Zhurnal.Pishi("подбор: " + g.Name + " (" + string.Join(", ", g.Exes) + "), экран " + hz + " Гц, RivaTuner " + (core.RtssInstalled ? "есть" : "нет"));
            if (!core.RtssInstalled) { status.Show("Сначала нажми «Включить» — нужен RivaTuner.", StatusLine.Tone.Warn); return; }
            podborStop = new CancellationTokenSource();
            var stop = podborStop.Token;
            Zanyat();
            int etap = 0;   // 1 — стоит временный профиль без лимита, 2 — лимит уже включён
            string itog = null; var tone = StatusLine.Tone.Ok;
            try
            {
                await Task.Run(() => core.ZamerProfile(g)); etap = 1;
                var bez = await Izmerit(g, "Запусти игру и 2 минуты играй как обычно — в бою, не в меню.", stop);
                int lim = Zamer.VybratLimit(bez.Derzhit, hz);
                await Task.Run(() => { core.VernutProfile(g, false); core.Enable(g, lim, t => { }); }); etap = 2;
                Itog s = null;
                try { s = await Izmerit(g, "Лимит " + lim + " поставлен. Запусти игру — проверю результат.", stop); }
                catch (OperationCanceledException) { }   // лимит уже стоит; проверку можно пропустить
                int poverh = bez.Poverh + (s != null ? s.Poverh : 0);
                itog = "Без лимита: 1% low " + Itog.Chislo(bez.Low1) + ", рывков " + Itog.Chislo(bez.RyvkovVMin) + " в минуту" +
                       (poverh > 10 ? " (оверлей поверх игры " + poverh + " с — не считал)" : "") + ".\n" +
                       (s == null ? "Поставил лимит " + lim + "." :
                        "С лимитом " + lim + ": 1% low " + Itog.Chislo(s.Low1) + ", рывков " + Itog.Chislo(s.RyvkovVMin) + " в минуту" + ".");
            }
            catch (OperationCanceledException) { itog = "Замер отменён."; tone = StatusLine.Tone.Info; }
            catch (Exception ex) { itog = ex.Message; tone = StatusLine.Tone.Error; Zhurnal.Pishi("подбор — сбой: " + ex); }
            Zhurnal.Pishi("подбор — итог: " + (itog ?? "").Replace("\n", " | "));
            if (etap == 1)
            {
                // вернуть профиль, каким он был
                try { await Task.Run(() => core.VernutProfile(g, true)); }
                catch (Exception ex) { itog += " " + ex.Message; tone = StatusLine.Tone.Warn; }
            }
            await Osvobodit(g, itog, tone);
        }

        // Свой лимит: если он ещё не стоит — ставим на ходу, потом замер с ним
        async void Proverit()
        {
            var g = list.Selected; if (g == null || busy) return;
            Zhurnal.Pishi("проверка: " + g.Name + " (" + string.Join(", ", g.Exes) + "), лимит " + fps + ", экран " + hz + " Гц");
            if (!core.RtssInstalled) { status.Show("Сначала нажми «Включить» — нужен RivaTuner.", StatusLine.Tone.Warn); return; }
            // «Без разрывов» держит частоту экрана — сверяем с ней, а не с числом в поле
            bool ss = core.St.Ssync.Contains(g.Key) && g.AppId != "730";
            int lim = ss ? hz : fps;
            podborStop = new CancellationTokenSource();
            var stop = podborStop.Token;
            Zanyat();
            string itog; var tone = StatusLine.Tone.Ok;
            try
            {
                int was;
                if (!core.St.On.TryGetValue(g.Key, out was) || (!ss && was != lim))
                {
                    await Task.Run(() => core.Enable(g, lim, t => { }));
                }
                var s = await Izmerit(g, "Лимит " + lim + " стоит. Запусти игру и 2 минуты играй как обычно.", stop);
                itog = (ss ? "Без разрывов (" + lim + " Гц)" : "С лимитом " + lim) + ": 1% low " + Itog.Chislo(s.Low1) + ", 0,1% low " + Itog.Chislo(s.Low01) + ", рывков " + Itog.Chislo(s.RyvkovVMin) + " в мин" + "." +
                       (s.Tochno ? "" : " (через RivaTuner — точнее с PresentMon)");
                // 1% low заметно ниже лимита — компьютер его в тяжёлых местах не держит
                if (s.Poverh > 10) itog += " Оверлей поверх игры " + s.Poverh + " с — не считал.";
                if (s.Low1 < lim * 0.95) { itog += "\nНе держит: поставь ниже или нажми «Подобрать по замеру»."; tone = StatusLine.Tone.Warn; }
                else itog += "\nДержит ровно.";
            }
            catch (OperationCanceledException) { itog = "Замер отменён."; tone = StatusLine.Tone.Info; }
            catch (Exception ex) { itog = ex.Message; tone = StatusLine.Tone.Error; Zhurnal.Pishi("проверка — сбой: " + ex); }
            Zhurnal.Pishi("проверка — итог: " + (itog ?? "").Replace("\n", " | "));
            await Osvobodit(g, itog, tone);
        }

        void Zanyat()
        {
            busy = true; minus.Enabled = plus.Enabled = list.Enabled = other.Enabled = podbor.Enabled = proverka.Enabled = off.Enabled = false;
            foreach (var c in chips) c.Enabled = false;
            UpdateButtons(); main.Enabled = true;
        }

        async Task Osvobodit(Game g, string itog, StatusLine.Tone tone)
        {
            // итог — ещё и в файл рядом с сырыми замерами
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Zamer.PmPath), "zamery");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "itogi.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + g.Name + "  " +
                    (itog ?? "").Replace("\n", " | ") + (Zamer.PoslednijCsv != null ? "  [" + Path.GetFileName(Zamer.PoslednijCsv) + "]" : "") + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
            podborStop = null; busy = false; list.Enabled = other.Enabled = true;
            foreach (var c in chips) c.Enabled = true;
            core.LoadState();
            var found = await Task.Run(() => core.ListGames());
            Fill(found, g.Key);
            status.Show(itog, tone);
        }

        // Ждёт игру, даёт минуту на загрузку и мерит 2 минуты, пока её окно впереди
        async Task<Itog> Izmerit(Game g, string text, CancellationToken stop)
        {
            // точный замер — PresentMon от Intel; качаем только с согласия, один раз
            if (!Zamer.PmEst && !pmOtkaz)
            {
                if (MessageBox.Show(this, "Для точного замера нужна бесплатная программа PresentMon от Intel (около 1 МБ, с GitHub). " +
                        "Она видит кадры сама, а не через RivaTuner, и заодно мерит задержку от мыши до экрана.\n\nСкачать PresentMon?",
                        "Ровный FPS", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    status.Show("Скачиваю PresentMon…", StatusLine.Tone.Info);
                    string oshibka = null;
                    try { await Task.Run(() => Zamer.SkachatPm()); }
                    catch (Exception ex) { oshibka = ex.Message; Zhurnal.Pishi("PresentMon не скачался: " + ex); }
                    if (oshibka != null) { status.Show(oshibka + " Мерю через RivaTuner.", StatusLine.Tone.Warn); pmOtkaz = true; await Task.Delay(2000); }
                }
                else pmOtkaz = true;
            }
            bool pm = Zamer.PmEst;
            bool uzheIdet = core.Running(g);
            status.Show(uzheIdet ? "Игра запущена — начинаю замер. Играй как обычно, окно держи впереди." : text, uzheIdet ? StatusLine.Tone.Info : StatusLine.Tone.Warn);
            Action<string> say = t => BeginInvoke((Action)(() => status.Show(t, StatusLine.Tone.Info)));
            var zaderzhki = new List<double>();
            var kadry = await Task.Run(() =>
            {
                // дождаться игру: PresentMon она нужна запущенной, а не видимой в RivaTuner (если RivaTuner к игре не
                // прицепился, старое ожидание висело бы вечно)
                Zhurnal.Pishi("замер: " + (pm ? "PresentMon" : "буфер RivaTuner") + ", игра " + (uzheIdet ? "уже запущена" : "ещё не запущена"));
                if (pm) { while (!core.Running(g)) { stop.ThrowIfCancellationRequested(); Thread.Sleep(1000); } }
                else Zamer.Sobrat(g.Exes, 0, t => { }, () => true, stop);
                Zhurnal.Pishi("замер: игра есть, " + (uzheIdet ? "10 с" : "минута") + " на загрузку");
                // минута на загрузку и вход в матч: экраны загрузки в замер не попадают
                if (!uzheIdet) say("Игра запущена. Минута на загрузку — потом замер.");
                for (int i = uzheIdet ? 10 : 60; i > 0; i--) { stop.ThrowIfCancellationRequested(); Thread.Sleep(1000); }
                Action<int> hod = t => say("Замеряю… " + t + " из 120 с. Играй как обычно и двигай мышью, окно игры держи впереди.");
                return pm ? Zamer.SobratPm(g.Exes, 120, hod, zaderzhki, stop) : Zamer.Sobrat(g.Exes, 120, hod, () => true, stop);
            }, stop);
            var it = Zamer.Poschitat(kadry);
            Zhurnal.Pishi("замер: кадров " + kadry.Count + (it == null ? " — мало" : ", средний " + Itog.Chislo(it.Sredniy) + ", 1% low " + Itog.Chislo(it.Low1) + ", держит " + Itog.Chislo(it.Derzhit)));
            if (it == null) throw new Exception("Кадров слишком мало — замер не получился, попробуй ещё раз.");
            it.Tochno = pm;
            it.Poverh = pm ? Zamer.PoverhSekund : 0;
            // Задержку мыши не показываем: AllInputToPhotonLatency в Deadlock 04.11 вышла 5 мс при 60 FPS (меньше кадра —
            // не верю), а DisplayLatency одинакова у RivaTuner и у лимита игры (19,0 и 19,6 мс) — цену лимита не различает.
            // Сырые столбцы остаются в CSV замера (zamery\).
            return it;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Обновления (Obnovlenie.cs): номер этой сборки и манифест в открытом репозитории.
        // Выпустить новую версию — vypustit.ps1 рядом (сам поднимает номер).
        public const int Versiya = 4;
        public const string ManifestUrl = "https://raw.githubusercontent.com/mrraketa/rovny-fps/main/versiya.txt";
        public const string SupportUrl = "https://boosty.to/raketo4ka/donate";

        [STAThread]
        static void Main(string[] args)
        {
            args = Obnovlenie.PosleZapuska(args);   // после обновления: дождаться выхода старой копии
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 2 && args[0] == "--test") { SelfTest.Run(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--shot")
            {
                // --shot <png> [номер игры] [версия обновления — показать кнопку «Обновить»]
                // 5-й ключ bez-rtss — снять окно так, будто RivaTuner не установлен
                if (args.Length > 3) shotUpdate = int.Parse(args[3]);
                shotBezRtss = args.Length > 4 && args[4] == "bez-rtss";
                Shot(args[1], args.Length > 2 ? int.Parse(args[2]) : 0); return;
            }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                // RivaTuner лежит в Program Files — без прав администратора туда не записать
                string a = string.Join(" ", args.Select(x => "\"" + x + "\""));
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, a) { Verb = "runas", UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (args.Length == 0)
                        MessageBox.Show("Без прав администратора я не смогу записать настройки RivaTuner — он лежит в Program Files.\n\nЗапусти ещё раз и нажми «Да».",
                            "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            // вторая копия перезаписала бы состояние первой и вместе с ней закрывала бы Steam
            bool first;
            var one = new Mutex(true, "Global\\RovnyFPS", out first);
            if (!first)
            {
                Vstraivanie.RazreshitVperedi();   // право выйти вперёд — первой копии, а от неё оболочке
                if (MainForm.AskFirstCopy()) return;
                if (args.Length == 0) MessageBox.Show("«Ровный FPS» уже открыт.", "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.ThreadException += (s, e) => Oops(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Oops(e.ExceptionObject as Exception);

            if (args.Length == 2 && args[0] == "--proverka") { RealCheck.Run(args[1]); return; }
            var core = new Core(); core.LoadState();
            Zhurnal.Pishi("=== Ровный FPS версия " + Versiya + ", " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " x64" : " x86") +
                ", экран " + Core.MonitorHz() + " Гц, RivaTuner " + (core.RtssInstalled ? core.RtssDir : "не установлен") +
                (Process.GetProcessesByName("RTSS").Length > 0 ? " (запущен)" : " (НЕ запущен)") + ", PresentMon " + (Zamer.PmEst ? "есть" : "нет") +
                ", аргументы: " + string.Join(" ", args));
            try { core.FixOldProfiles(); } catch { }
            ThreadPool.QueueUserWorkItem(delegate { core.RefreshAutostart(); });
            if (args.Length >= 2 && (args[0] == "--off" || args[0] == "--on"))
            {
                // без окна: --off <appid> | --on <appid> <fps>
                var g = core.ListGames().FirstOrDefault(x => x.Key == args[1]);
                if (g == null) return;
                if (args[0] == "--off" && core.St.On.ContainsKey(g.Key)) core.Disable(g);
                if (args[0] == "--on" && args.Length == 3) core.Enable(g, int.Parse(args[2]), s => { });
                return;
            }
            Application.Run(new MainForm(core));
            GC.KeepAlive(one);
        }

        static void Oops(Exception ex)
        {
            Zhurnal.Pishi("СБОЙ: " + ex);
            MessageBox.Show("Что-то пошло не так:\n\n" + (ex != null ? ex.Message : "неизвестная ошибка") +
                "\n\nRivaTuner и Steam можно просто перезапустить — настройки игр сохранены.", "Ровный FPS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static int shotUpdate;
        static bool shotBezRtss;
        static void Shot(string png, int select)
        {
            var core = new Core();
            if (shotBezRtss) core.RtssDir = Path.Combine(Path.GetTempPath(), "net-rivatuner");
            var f = new MainForm(core);
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-5000, -5000); f.ShowInTaskbar = false;
            f.LoadNow(); f.SelectForShot(select);
            f.Show(); Application.DoEvents();
            if (shotUpdate > 0) { f.PokazatObnovlenie(new Obnovlenie.Est { Versiya = shotUpdate }); Application.DoEvents(); }
            using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(png); }
            f.Close();
        }
    }

    // Проверка вживую: CS2 включить и выключить (со Steam), Deadlock выключить и включить обратно.
    // В конце всё как было до проверки.
    static class RealCheck
    {
        static StringBuilder log = new StringBuilder();
        static void W(string s) { log.AppendLine(DateTime.Now.ToString("HH:mm:ss ") + s); }
        static void Check(string what, bool ok) { W((ok ? "OK   " : "FAIL ") + what); }

        static bool Task() { return Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"RovnyFPS - RivaTuner\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }).Let(p => { p.WaitForExit(); return p.ExitCode == 0; }); }
        static bool Running(string n) { return Process.GetProcessesByName(n).Length > 0; }
        static bool WaitFor(string n, int sec) { for (int i = 0; i < sec * 2; i++) { if (Running(n)) return true; Thread.Sleep(500); } return false; }

        public static void Run(string logPath)
        {
            try
            {
                var c = new Core(); c.LoadState(); c.Trace = s => W("     " + s);
                Action<string> say = s => W("  [окно] " + s);
                var cs = new Game { Key = "730", AppId = "730", Name = "CS2", Exes = { "cs2.exe" } };
                var dl = c.ListGames().FirstOrDefault(g => g.AppId == "1422450");
                string prof = c.ProfilesDir;
                if (c.St.On.ContainsKey("730")) { W("CS2 уже включён — проверку не трогаю"); return; }
                bool steam0 = Running("steam");
                W("До: Steam " + (steam0 ? "работает" : "закрыт") + ", RTSS " + (Running("RTSS") ? "работает" : "закрыт") + ", задание " + Task() + ", Deadlock включён " + (dl != null && c.St.On.ContainsKey(dl.Key)));

                W("== CS2: включаю");
                W("  " + c.Enable(cs, 240, say));
                Check("профиль cs2.exe.cfg с лимитом 240", File.Exists(Path.Combine(prof, "cs2.exe.cfg")) && File.ReadAllText(Path.Combine(prof, "cs2.exe.cfg")).Contains("Limit=240"));
                foreach (var f in c.LocalConfigs())
                    Check("параметры запуска записаны (" + f.Split('\\')[f.Split('\\').Length - 3] + ")", Vdf.GetLaunchOptions(File.ReadAllText(f), "730") == Core.MergeFlags(null));
                if (steam0) { Check("Steam снова запущен", WaitFor("steam", 60)); Thread.Sleep(25000); }
                Check("RTSS работает", WaitFor("RTSS", 10));

                W("== CS2: выключаю (видно, сохранил ли Steam наши параметры у себя)");
                W("  " + c.Disable(cs));
                Check("профиль cs2 удалён", !File.Exists(Path.Combine(prof, "cs2.exe.cfg")));
                foreach (var f in c.LocalConfigs())
                    Check("параметры запуска вернулись к пустым", (Vdf.GetLaunchOptions(File.ReadAllText(f), "730") ?? "") == "");
                if (steam0) Check("Steam снова запущен", WaitFor("steam", 60));
                Check("задание осталось (Deadlock включён)", Task());

                if (dl != null && c.St.On.ContainsKey(dl.Key))
                {
                    int fps = c.St.On[dl.Key];
                    W("== Deadlock: выключаю последнюю игру");
                    W("  " + c.Disable(dl));
                    Check("профиль deadlock удалён", !File.Exists(Path.Combine(prof, "deadlock.exe.cfg")));
                    Check("задание автозапуска удалено", !Task());
                    W("== Deadlock: включаю обратно с " + fps);
                    W("  " + c.Enable(dl, fps, say));
                    Check("профиль deadlock на месте", File.Exists(Path.Combine(prof, "deadlock.exe.cfg")) && File.ReadAllText(Path.Combine(prof, "deadlock.exe.cfg")).Contains("Limit=" + fps));
                    Check("задание автозапуска снова есть", Task());
                    Check("RTSS работает", WaitFor("RTSS", 10));
                }
                c.LoadState();
                Check("в состоянии только то, что было: " + string.Join(",", c.St.On.Keys), !c.St.On.ContainsKey("730") && c.St.Orig.Count == 0);
            }
            catch (Exception ex) { W("ОШИБКА: " + ex); }
            finally { W("конец"); File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false)); }
        }

        static T Let<T>(this Process p, Func<Process, T> f) { return f(p); }
    }

    // Проверка на копиях: ничего настоящего не трогает
    static class SelfTest
    {
        public static void Run(string dir)
        {
            var log = new StringBuilder();
            try
            {
                var c = new Core();
                log.AppendLine("RTSS: " + c.RtssDir + " есть=" + c.RtssInstalled);
                log.AppendLine("Steam: " + c.SteamDir + "  NVIDIA=" + Core.HasNvidia());
                c.StatePath = Path.Combine(dir, "sostoyanie.txt");
                c.LoadState();
                foreach (var g in c.ListGames()) log.AppendLine("  " + (g.Store ?? (g.AppId != null ? "Steam" : "своя")) + " | " + g.Key + " | " + g.Name + " | " + string.Join(", ", g.Exes) + " | fps по умолчанию " + c.DefaultFps(g));

                // подбор лимита: Deadlock 04.11 без лимита 1% low 109 → на 60 Гц 60, на 144 Гц 100
                Check(log, "подбор: 60 Гц, 1% low 109 → 60", Zamer.VybratLimit(109.1, 60) == 60);
                Check(log, "подбор: 144 Гц, 1% low 109 → 100", Zamer.VybratLimit(109.1, 144) == 100);
                Check(log, "подбор: 144 Гц, 1% low 300 → 141", Zamer.VybratLimit(300, 144) == 141);
                Check(log, "подбор: слабый ПК, 1% low 20 → 30", Zamer.VybratLimit(20, 60) == 30);
                // Deadlock 04.11 19:30: ~285 FPS, 1% кадров по 26 мс — держит постоянно ~280, лимит на 60 Гц = 60, а не 35
                var ryvkami = Enumerable.Range(0, 10000).Select(i => i % 100 == 0 ? 26000 : 3500).ToList();   // рывки вразброс, как в игре
                var ir = Zamer.Poschitat(ryvkami);
                Check(log, "подбор по рывкам: 1% low " + Itog.Chislo(ir.Low1) + ", держит " + Itog.Chislo(ir.Derzhit) + " → лимит " + Zamer.VybratLimit(ir.Derzhit, 60),
                    ir.Low1 < 50 && ir.Derzhit > 200 && Zamer.VybratLimit(ir.Derzhit, 60) == 60);
                var kadry = Enumerable.Repeat(16667, 9900).Concat(Enumerable.Repeat(33000, 90)).Concat(Enumerable.Repeat(80000, 10)).ToList();
                var it = Zamer.Poschitat(kadry);
                Check(log, "замер: 1% low " + Itog.Chislo(it.Low1) + ", 0,1% low " + Itog.Chislo(it.Low01), Math.Abs(it.Low1 - 30.3) < 0.1 && Math.Abs(it.Low01 - 12.5) < 0.1);
                Check(log, "замер: рывков в минуту " + Itog.Chislo(it.RyvkovVMin), Math.Abs(it.RyvkovVMin - 10 / (kadry.Sum(x => (double)x) / 6e7)) < 0.01);
                // «Без разрывов»: Scanline Sync вместо числа, Reflex выключен
                string ps = Core.Profile(60, false, true), po = Core.Profile(60, false);
                Check(log, "без разрывов: Limit=0, SyncScanline0=-100, SyncTimeout=1, SyncFlush=1",
                    ps.Contains("Limit=0\r\n") && ps.Contains("SyncScanline0=-100") && ps.Contains("SyncTimeout=1") && ps.Contains("SyncFlush=1") && ps.Contains("SyncLimiter=0"));
                Check(log, "обычный профиль без Scanline Sync", po.Contains("Limit=60\r\n") && po.Contains("SyncScanline0=0") && po.Contains("SyncTimeout=0"));
                var stp = Path.Combine(dir, "ssync-state.txt");
                var st1 = State.Load(stp); st1.Ssync.Add("1422450"); st1.Save();
                Check(log, "без разрывов запоминается", State.Load(stp).Ssync.Contains("1422450"));
                // VDF на копии настоящего localconfig
                var src = Directory.GetFiles(Path.Combine(c.SteamDir, "userdata"), "localconfig.vdf", SearchOption.AllDirectories).Where(f => !f.Contains("\\0\\")).First();
                string orig = File.ReadAllText(src, new UTF8Encoding(false));
                string t1 = Vdf.SetLaunchOptions(orig, "730", Core.MergeFlags(null));
                Check(log, "CS2 вставлен блоком", Vdf.GetLaunchOptions(t1, "730") == Core.MergeFlags(null));
                Check(log, "остальное не тронуто", t1.Length - orig.Length < 200 && Vdf.GetLaunchOptions(t1, "570") == Vdf.GetLaunchOptions(orig, "570"));
                string t2 = Vdf.SetLaunchOptions(t1, "730", Core.MergeFlags("-novid +fps_max 400 -high"));
                Check(log, "слияние флагов: " + Vdf.GetLaunchOptions(t2, "730"), Vdf.GetLaunchOptions(t2, "730") == "-novid -mainthreadpriority 3 -high -noreflex +fps_max 0 -allow_third_party_software");
                string t3 = Vdf.SetLaunchOptions(t2, "570", "-test");
                Check(log, "LaunchOptions в существующую игру", Vdf.GetLaunchOptions(t3, "570") == "-test" && Vdf.GetLaunchOptions(t3, "730") != null);
                string t4 = Vdf.SetLaunchOptions(t3, "730", "");
                Check(log, "откат до пустого", Vdf.GetLaunchOptions(t4, "730") == "");
                File.WriteAllText(Path.Combine(dir, "localconfig.after.vdf"), t3, new UTF8Encoding(false));

                // Профили и откат — в копию папки Profiles
                c.ProfilesDir = Path.Combine(dir, "Profiles"); Directory.CreateDirectory(c.ProfilesDir);
                c.RtssDir = dir; File.WriteAllText(Path.Combine(dir, "RTSS.exe"), "");
                c.SteamDir = Path.Combine(dir, "steam"); var ud = Path.Combine(c.SteamDir, @"userdata\123\config"); Directory.CreateDirectory(ud);
                File.WriteAllText(Path.Combine(ud, "localconfig.vdf"), orig, new UTF8Encoding(false));
                c.TestMode = true;
                File.WriteAllText(Path.Combine(c.ProfilesDir, "bg3.exe.cfg"), "чужой профиль");
                var bg = new Game { Key = "1086940", AppId = "1086940", Name = "BG3", Exes = { "bg3.exe", "bg3_dx11.exe" } };
                log.AppendLine("  " + c.Enable(bg, 141, s => { }));
                Check(log, "профиль записан с лимитом", File.ReadAllText(Path.Combine(c.ProfilesDir, "bg3_dx11.exe.cfg")).Contains("Limit=141"));
                Check(log, "не CS2 — обычный лимитер, без Reflex", File.ReadAllText(Path.Combine(c.ProfilesDir, "bg3_dx11.exe.cfg")).Contains("SyncLimiter=0\r\nSyncDisplay=0\r\nReflexSleep=0"));
                Check(log, "чужой профиль сохранён в .bak", File.Exists(Path.Combine(c.ProfilesDir, "bg3.exe.cfg.rovnyfps.bak")));
                c.Disable(bg);
                Check(log, "чужой профиль вернулся", File.ReadAllText(Path.Combine(c.ProfilesDir, "bg3.exe.cfg")) == "чужой профиль" && !File.Exists(Path.Combine(c.ProfilesDir, "bg3_dx11.exe.cfg")));
                var cs = new Game { Key = "730", AppId = "730", Name = "CS2", Exes = { "cs2.exe" } };
                c.Enable(cs, 240, s => { });
                var lc = Path.Combine(ud, "localconfig.vdf");
                Check(log, "CS2: параметры запуска записаны", Vdf.GetLaunchOptions(File.ReadAllText(lc), "730") == Core.MergeFlags(null));
                Check(log, "CS2: лимитер через Reflex, как в гайде", File.ReadAllText(Path.Combine(c.ProfilesDir, "cs2.exe.cfg")).Contains("SyncLimiter=3"));
                c.Enable(cs, 200, s => { });
                Check(log, "повторное включение не плодит флаги", Vdf.GetLaunchOptions(File.ReadAllText(lc), "730") == Core.MergeFlags(null));
                c.Disable(cs);
                Check(log, "CS2: откат параметров", Vdf.GetLaunchOptions(File.ReadAllText(lc), "730") == "");
                Check(log, "состояние пустое после отката", c.St.On.Count == 0 && c.St.Orig.Count == 0);

                // откат убирает только наши ключи и возвращает его прежние
                string um = Core.Unmerge(Core.MergeFlags("+fps_max 400 -novid") + " -fullscreen", "+fps_max 400 -novid");
                Check(log, "откат бережёт его ключи: " + um, um == "-novid -fullscreen +fps_max 400");
                // он сам дописал ключ после нас — выключение его не стирает
                c.Enable(cs, 240, s => { });
                var t5 = File.ReadAllText(lc);
                File.WriteAllText(lc, Vdf.SetLaunchOptions(t5, "730", Vdf.GetLaunchOptions(t5, "730") + " -novid"), new UTF8Encoding(false));
                c.Disable(cs);
                Check(log, "дописанное им после нас осталось", Vdf.GetLaunchOptions(File.ReadAllText(lc), "730") == "-novid");

                // один exe на две игры — отказ
                c.Enable(bg, 100, s => { });
                bool refused = false;
                try { c.Enable(new Game { Key = "custom:x", Name = "X", Exes = { "bg3.exe" } }, 60, s => { }); } catch (Exception) { refused = true; }
                Check(log, "занятый exe другой игрой — отказ", refused);
                // игра пропала из списка — всё равно видна, чтобы выключить
                c.St.On["999999"] = 60; c.St.OnExes["999999"] = "gone.exe";
                Check(log, "пропавшая включённая игра в списке", c.ListGames().Any(g => g.Key == "999999" && g.Store == "не найдена"));
                c.St.On.Remove("999999"); c.St.OnExes.Remove("999999");
                c.Disable(bg);

                var xml = Core.TaskXml(@"C:\Program Files (x86)\RivaTuner Statistics Server", "PC\\user");
                Check(log, "задание: без лимита 72 ч и работает от батареи",
                    xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>") && xml.Contains("<DisallowStartIfOnBatteries>false") && xml.Contains("<StopIfGoingOnBatteries>false"));

                // данные разведки: служебные exe Source 2 / Cyberpunk не принимаются за игру
                var fake = Path.Combine(dir, "src2"); Directory.CreateDirectory(Path.Combine(fake, @"game\bin\win64"));
                File.WriteAllBytes(Path.Combine(fake, @"game\bin\win64\vconsole2.exe"), new byte[5000]);
                File.WriteAllBytes(Path.Combine(fake, @"game\bin\win64\mygame.exe"), new byte[1000]);
                Check(log, "vconsole2.exe не принят за игру", Core.GuessExe(fake) == "mygame.exe");
                Check(log, "Elden Ring: потолок 60 и предупреждение",
                    c.DefaultFps(new Game { Key = "1245620x", Exes = { "eldenring.exe" } }) <= 60 && Core.WarningFor(new Game { Exes = { "eldenring.exe" } }) != null);
                Check(log, "Valorant: предупреждение про Vanguard", (Core.WarningFor(new Game { Exes = { "VALORANT-Win64-Shipping.exe" } }) ?? "").Contains("Vanguard"));

                // битое состояние не роняет программу
                File.WriteAllText(c.StatePath, "on\tx\t99999999999\tq.exe\norig\t730|1\t!!!\nfps\t570\t60\n");
                var st = State.Load(c.StatePath);
                Check(log, "битое состояние читается", st.Fps.ContainsKey("570") && !st.On.ContainsKey("x"));
            }
            catch (Exception ex) { log.AppendLine("ОШИБКА: " + ex); }
            TestLaunchers(log, dir);
            File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString(), new UTF8Encoding(false));
        }

        // Другие лаунчеры на подложных данных: манифесты Epic в папке, реестр — списками
        static void TestLaunchers(StringBuilder log, string dir)
        {
            try
            {
                var root = Path.Combine(dir, "launchers");
                var man = Path.Combine(root, "Manifests"); Directory.CreateDirectory(man);
                var fort = Path.Combine(root, @"games\Fortnite"); var bin = Path.Combine(fort, @"FortniteGame\Binaries\Win64");
                Fake(Path.Combine(bin, "FortniteLauncher.exe"), 10); Fake(Path.Combine(bin, "FortniteClient-Win64-Shipping.exe"), 2000);
                Fake(Path.Combine(bin, "CrashReportClient.exe"), 5000);
                var cafe = Path.Combine(root, @"games\Cafe"); Fake(Path.Combine(cafe, "Cafe.exe"), 100);
                Item(man, "a.item", "Fortnite", "Fortnite", fort, "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "\"public\",\"games\",\"applications\"", "Fortnite", false);
                Item(man, "a-copy.item", "Fortnite", "Fortnite", fort, "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "\"games\"", "Fortnite", false);
                Item(man, "dlc.item", "FortDlc", "Fortnite DLC", fort, "", "\"public\",\"addons\"", "Fortnite", false);
                Item(man, "ue.item", "UE_5.4", "Unreal Engine 5.4", fort, "Engine/Binaries/Win64/UnrealEditor.exe", "\"public\",\"engines\"", "UE_5.4", false);
                Item(man, "half.item", "Half", "Недокачанная", cafe, "Cafe.exe", "\"games\"", "Half", true);
                Item(man, "gone.item", "Gone", "Удалённая", Path.Combine(root, "nope"), "x.exe", "\"games\"", "Gone", false);
                Item(man, "cafe.item", "Cafe", "Caf\\u00e9 \\\"Deluxe\\\"", cafe, "Cafe.exe", "\"games\"", "Cafe", false);
                File.WriteAllText(Path.Combine(man, "bad.item"), "{ \"DisplayName\": \"Битый");
                var epic = Core.EpicGames(man);
                Check(log, "Epic: из 8 манифестов прошли 3 — Fortnite дважды и Café (DLC, движок, недокачанная, удалённая, битый отсеяны)", epic.Count == 3);
                var fn = epic.FirstOrDefault(g => g.Key == "epic:Fortnite");
                Check(log, "Epic: Fortnite → " + (fn == null ? "нет" : string.Join(", ", fn.Exes)),
                    fn != null && fn.Store == "Epic" && fn.AppId == null && fn.Exes.Count == 1 && fn.Exes[0] == "FortniteClient-Win64-Shipping.exe");
                var cf = epic.FirstOrDefault(g => g.Key == "epic:Cafe");
                Check(log, "Epic: название из JSON разобрано: " + (cf == null ? "нет" : cf.Name), cf != null && cf.Name == "Café \"Deluxe\"" && cf.Exes.SequenceEqual(new[] { "Cafe.exe" }));

                var wit = Path.Combine(root, @"games\The Witcher"); Fake(Path.Combine(wit, "witcher.exe"), 300); Fake(Path.Combine(wit, "unins000.exe"), 900);
                var gog = new List<Dictionary<string, string>> {
                    D("_key", "1207658924", "gameID", "1207658924", "gameName", "The Witcher", "path", wit, "exe", Path.Combine(wit, "witcher.exe")),
                    D("_key", "1207658924", "gameID", "1207658924", "gameName", "The Witcher", "path", wit, "exe", Path.Combine(wit, "witcher.exe")),
                    D("_key", "111", "gameID", "111", "gameName", "The Witcher DLC", "path", wit, "dependsOn", "1207658924"),
                    D("_key", "222", "gameName", "Без папки") };
                var gg = Core.GogGames(gog);
                Check(log, "GOG: DLC и запись без папки отсеяны", gg.Count == 2 && gg.All(g => g.Key == "gog:1207658924"));
                Check(log, "GOG: exe из реестра, не деинсталлятор", gg.Count > 0 && gg[0].Exes.SequenceEqual(new[] { "witcher.exe" }) && gg[0].Store == "GOG");

                var ea = Path.Combine(root, @"games\Fake Battle"); Fake(Path.Combine(ea, "FakeBattle.exe"), 400); Fake(Path.Combine(ea, "EAAntiCheat.GameServiceLauncher.exe"), 900);
                var ow = Path.Combine(root, @"games\Overwatch"); Fake(Path.Combine(ow, "Overwatch Launcher.exe"), 900); Fake(Path.Combine(ow, @"_retail_\Overwatch.exe"), 500);
                var val = Path.Combine(root, @"games\VALORANT\live"); Fake(Path.Combine(val, "VALORANT.exe"), 50);
                Fake(Path.Combine(val, @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"), 900);
                var fc = Path.Combine(root, @"games\Far Cry 5"); Fake(Path.Combine(fc, @"bin\FarCry5.exe"), 700); Fake(Path.Combine(fc, @"bin\UplayWebCore.exe"), 10);
                var un = new List<Dictionary<string, string>> {
                    D("_key", "{EA-1}", "DisplayName", "Fake Battle", "InstallLocation", ea + "\\", "UninstallString", "\"C:\\Program Files\\Common Files\\EAInstaller\\Fake Battle\\Cleanup.exe\" uninstall_game -id \"Fake Battle\""),
                    D("_key", "Overwatch", "DisplayName", "Overwatch", "InstallLocation", ow, "UninstallString", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=prometheus --displayname=\"Overwatch\""),
                    D("_key", "Battle.net", "DisplayName", "Battle.net", "InstallLocation", ow, "UninstallString", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=battle.net"),
                    D("_key", "Riot Game valorant.live", "DisplayName", "VALORANT", "InstallLocation", val, "UninstallString", "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live"),
                    D("_key", "Notepad++", "DisplayName", "Notepad++", "InstallLocation", fc, "UninstallString", "C:\\npp\\uninstall.exe"),
                    D("_key", "Uplay Install 5595", "DisplayName", "Far Cry 5", "UninstallString", "\"C:\\Uplay\\Uplay.exe\" uplay://uninstall/5595") };
                var ubi = new List<Dictionary<string, string>> { D("_key", "5595", "InstallDir", fc.Replace('\\', '/') + "/") };

                var c = new Core();
                c.SteamDir = null; c.EpicManifestsDir = man;
                c.GogSource = () => gog; c.UninstallSource = () => un; c.UbiSource = () => ubi;
                c.StatePath = Path.Combine(root, "sostoyanie.txt"); c.LoadState();
                var all = c.ListGames();
                foreach (var g in all) log.AppendLine("    " + g.Store + " | " + g.Key + " | " + g.Name + " | " + string.Join(", ", g.Exes));
                Check(log, "дубли из одного источника схлопнуты", all.Count(g => g.Key == "epic:Fortnite") == 1 && all.Count(g => g.Key == "gog:1207658924") == 1);
                var e1 = all.FirstOrDefault(g => g.Key == "ea:{EA-1}");
                Check(log, "EA: игра найдена, античит не взят", e1 != null && e1.Store == "EA" && e1.Exes.SequenceEqual(new[] { "FakeBattle.exe" }));
                var b1 = all.FirstOrDefault(g => g.Key == "bnet:prometheus");
                Check(log, "Battle.net: игра без лаунчера-обёртки, сам Battle.net не в списке",
                    b1 != null && b1.Exes.SequenceEqual(new[] { "Overwatch.exe" }) && !all.Any(g => g.Key.StartsWith("bnet:battle")));
                var v1 = all.FirstOrDefault(g => g.Key == "riot:valorant.live");
                Check(log, "Riot: VALORANT → Shipping.exe", v1 != null && v1.Store == "Riot" && v1.Exes.SequenceEqual(new[] { "VALORANT-Win64-Shipping.exe" }));
                var u1 = all.FirstOrDefault(g => g.Key == "ubi:5595");
                Check(log, "Ubisoft: название из записи удаления, exe из папки", u1 != null && u1.Name == "Far Cry 5" && u1.Exes.SequenceEqual(new[] { "FarCry5.exe" }));
                Check(log, "чужие программы не попали", !all.Any(g => g.Name == "Notepad++"));
                Check(log, "всего 7 строк: Epic 2 + GOG 1 + EA 1 + Battle.net 1 + Riot 1 + Ubisoft 1", all.Count == 7);

                // Ничего нет или всё битое — список строится, без исключений
                var bad = new Core();
                bad.SteamDir = Path.Combine(root, "steam-net"); bad.EpicManifestsDir = Path.Combine(root, "net-takoy");
                Directory.CreateDirectory(Path.Combine(bad.SteamDir, "steamapps"));
                File.WriteAllText(Path.Combine(bad.SteamDir, @"steamapps\libraryfolders.vdf"), "\"path\" \"Q:\\нет\"");
                File.WriteAllText(Path.Combine(bad.SteamDir, @"steamapps\appmanifest_1.acf"), "мусор");
                bad.GogSource = () => { throw new IOException("тест: реестр недоступен"); };
                bad.UninstallSource = () => null; bad.UbiSource = () => null;
                bad.StatePath = Path.Combine(root, "sostoyanie2.txt"); bad.LoadState();
                var none = bad.ListGames();
                Check(log, "битые и пустые источники не роняют список", none.Count == 0);
            }
            catch (Exception ex) { log.AppendLine("FAIL лаунчеры: " + ex); }
        }

        static void Fake(string path, int bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[bytes]);
        }

        static void Item(string dir, string file, string app, string nameJson, string loc, string exe, string cats, string main, bool incomplete)
        {
            File.WriteAllText(Path.Combine(dir, file),
                "{\r\n\t\"FormatVersion\": 0,\r\n\t\"bIsIncompleteInstall\": " + (incomplete ? "true" : "false") + ",\r\n" +
                "\t\"LaunchExecutable\": \"" + exe + "\",\r\n\t\"DisplayName\": \"" + nameJson + "\",\r\n" +
                "\t\"InstallLocation\": \"" + loc.Replace("\\", "\\\\") + "\",\r\n\t\"AppCategories\": [ " + cats + " ],\r\n" +
                "\t\"AppName\": \"" + app + "\",\r\n\t\"MainGameAppName\": \"" + main + "\",\r\n\t\"ExpectingDLCInstalled\": {}\r\n}", new UTF8Encoding(false));
        }

        static Dictionary<string, string> D(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        static void Check(StringBuilder log, string what, bool ok) { log.AppendLine((ok ? "OK   " : "FAIL ") + what); }
    }
}
