// Obnovlenie.cs — кнопка «Обновить» для наших программ на C# (.NET Framework 4.x, WinForms или WPF).
// Модуль нейтральный: ничего не знает о программе, его можно класть в любую.
//
// Откуда берётся обновление — текстовый манифест по постоянной ссылке
// (например raw.githubusercontent.com/<владелец>/<репо>/main/versiya.txt):
//   1-я строка — номер версии, целое число;
//   2-я — прямая ссылка на новый exe;
//   3-я — SHA-256 этого exe (hex), чтобы недокачанный файл не встал на место;
//   дальше — что нового, по строке на пункт (можно пусто).
//
// Как подключить:
//   1. В самом начале Main: args = Obnovlenie.PosleZapuska(args);
//      (ждёт, пока закроется старая копия, и убирает .old; ключ --posle-obnovleniya вырезается из args)
//   2. В фоне: var est = Obnovlenie.Proverit(ManifestUrl, Versiya); — null, если обновления нет или сети нет.
//   3. Есть — показать кнопку «Обновить до версии N»; по нажатию в фоне Obnovlenie.Skachat(est),
//      потом в UI-потоке Obnovlenie.Perezapustit() и закрыть программу (Application.Exit()).
//
// Замена себя: запущенный exe Windows разрешает переименовать, но не перезаписать.
// Поэтому: новый файл качается рядом как <exe>.new, старый переименовывается в <exe>.old,
// новый встаёт на место и запускается с --posle-obnovleniya <pid>; новая копия ждёт выхода
// старой и удаляет .old. Если программа лежит там, куда без прав не записать (Program Files),
// Skachat бросит понятную ошибку — такие программы пусть обновляются с правами администратора.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public static class Obnovlenie
{
    public class Est
    {
        public int Versiya;
        public string Url, Sha256;
        public List<string> ChtoNovogo = new List<string>();
    }

    const string Klyuch = "--posle-obnovleniya";
    static string Exe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
    static string[] zapusk = new string[0];

    // Вызвать первой строкой Main. Возвращает args без служебного ключа.
    public static string[] PosleZapuska(string[] args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == Klyuch && i + 1 < args.Length)
            {
                int pid;
                if (int.TryParse(args[++i], out pid))
                    try { Process.GetProcessById(pid).WaitForExit(15000); } catch (ArgumentException) { }   // уже вышла
                continue;
            }
            rest.Add(args[i]);
        }
        // .old мог остаться занятым на долю секунды — несколько попыток, потом не мешаем запуску
        string old = Exe + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); } catch (Exception) { Thread.Sleep(300); }
        }
        zapusk = rest.ToArray();
        return zapusk;
    }

    // null — обновления нет, манифест не найден или нет сети (программа работает дальше как есть)
    public static Est Proverit(string manifestUrl, int tekushaya)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2 на старых Windows
            string text;
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                // метка против кэша GitHub; у file:/// её быть не может
                bool web = manifestUrl.StartsWith("http");
                text = wc.DownloadString(web ? manifestUrl + (manifestUrl.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks : manifestUrl);
            }
            var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            if (lines.Count < 3) return null;
            var e = new Est { Url = lines[1], Sha256 = lines[2].ToLowerInvariant() };
            if (!int.TryParse(lines[0].TrimStart('﻿'), out e.Versiya)) return null;
            // file:/// — только для проверки на этом ПК (манифест и exe в локальной папке)
            if (e.Versiya <= tekushaya || !(e.Url.StartsWith("https://") || e.Url.StartsWith("file:///")) || e.Sha256.Length != 64) return null;
            e.ChtoNovogo = lines.Skip(3).Where(l => l != "").ToList();
            return e;
        }
        catch (Exception) { return null; }
    }

    // Долгое — вызывать в фоне. Качает, сверяет SHA-256 и ставит новый exe на место старого.
    public static void Skachat(Est e)
    {
        string exe = Exe, neu = exe + ".new", old = exe + ".old";
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        try { File.Delete(neu); } catch (Exception) { }
        try
        {
            using (var wc = new WebClient()) wc.DownloadFile(e.Url, neu);
        }
        catch (WebException ex) { throw new Exception("Не скачалось: " + ex.Message); }
        catch (UnauthorizedAccessException) { throw new Exception("В папку программы не записать — нужны права администратора."); }

        string sha;
        using (var s = File.OpenRead(neu)) using (var h = SHA256.Create())
            sha = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        if (sha != e.Sha256) { try { File.Delete(neu); } catch (Exception) { } throw new Exception("Файл скачался битым — попробуй ещё раз."); }

        try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { }
        try
        {
            File.Move(exe, old);
            File.Move(neu, exe);
        }
        catch (Exception ex)
        {
            // вернуть как было: программа должна запускаться и после неудачи
            if (!File.Exists(exe) && File.Exists(old)) try { File.Move(old, exe); } catch (Exception) { }
            throw new Exception("Не получилось заменить файл программы: " + ex.Message);
        }
    }

    // После Skachat: запустить новую копию с теми же ключами. Саму программу закрывает вызывающий.
    public static void Perezapustit()
    {
        string a = string.Join(" ", zapusk.Select(x => "\"" + x + "\"").Concat(new[] { Klyuch, Process.GetCurrentProcess().Id.ToString() }));
        Process.Start(new ProcessStartInfo(Exe, a) { UseShellExecute = false });
    }
}
