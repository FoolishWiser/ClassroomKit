using System;
using System.Collections.Generic;
using System.IO;
using System.Resources;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace ClassroomKit.Agent.Desktop;

/// <summary>
/// 界面字体：优先用随包内嵌的两枚 HarmonyOS Sans SC（WPF Resource，不安装到系统）。
/// 依次尝试几种 pack URI 形态，都取不到时把内嵌的 ttf 抽到本地缓存目录按文件加载；
/// 再失败才回退微软雅黑，并把每一步的原因记进 <see cref="Diagnostic"/> 供日志排查。
/// </summary>
internal static class FontCatalog
{
    public const string FamilyName = "HarmonyOS Sans SC";
    public const string Fallback = "Microsoft YaHei UI";

    private const string RegularResource = "assets/fonts/harmonyos_sanssc_regular.ttf";
    private const string SemiboldResource = "assets/fonts/harmonyos_sanssc_semibold.ttf";

    private static readonly string[] PackCandidates =
    {
        "pack://application:,,,/Assets/Fonts/#" + FamilyName,
        "pack://application:,,,/ClassroomKit.Agent;component/Assets/Fonts/#" + FamilyName,
        "pack://application:,,,/Assets/Fonts/HarmonyOS_SansSC_Regular.ttf#" + FamilyName,
        "pack://application:,,,/ClassroomKit.Agent;component/Assets/Fonts/HarmonyOS_SansSC_Regular.ttf#" + FamilyName
    };

    /// <summary>上一次 <see cref="Resolve"/> 的结果说明（成功或每一步失败原因）。</summary>
    public static string Diagnostic { get; private set; } = "";

    public static FontFamily Resolve(out bool embedded)
    {
        var notes = new List<string>();

        foreach (var uri in PackCandidates)
        {
            try
            {
                var family = new FontFamily(uri);
                var faces = 0;
                foreach (var typeface in family.GetTypefaces())
                {
                    faces++;
                    if (typeface.TryGetGlyphTypeface(out _))
                    {
                        embedded = true;
                        Diagnostic = "pack " + Short(uri);
                        return family;
                    }
                }

                notes.Add(Short(uri) + "→" + faces + " 个字形");
            }
            catch (Exception ex)
            {
                notes.Add(Short(uri) + "→" + ex.GetType().Name);
            }
        }

        try
        {
            var dir = ExtractFonts();
            if (dir is not null)
            {
                var family = new FontFamily(new Uri(dir, UriKind.Absolute), "./#" + FamilyName);
                foreach (var typeface in family.GetTypefaces())
                {
                    if (typeface.TryGetGlyphTypeface(out _))
                    {
                        embedded = true;
                        Diagnostic = "file " + dir;
                        return family;
                    }
                }

                notes.Add("临时文件→0 个字形");
            }
            else
            {
                notes.Add("临时文件→没有可用资源");
            }
        }
        catch (Exception ex)
        {
            notes.Add("临时文件→" + ex.GetType().Name + ": " + ex.Message);
        }

        embedded = false;
        Diagnostic = string.Join("；", notes);
        return new FontFamily(Fallback);
    }

    private static string Short(string uri)
    {
        var index = uri.IndexOf("/#", StringComparison.Ordinal);
        if (index > 0) uri = uri.Substring(0, index + 1);
        var slash = uri.LastIndexOf('/');
        return slash >= 0 ? uri.Substring(slash + 1) : uri;
    }

    /// <summary>把内嵌 ttf 复制到 %LOCALAPPDATA%\ClassroomKit\fonts（已存在且大小一致就复用）。</summary>
    private static string? ExtractFonts()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassroomKit", "fonts");
        Directory.CreateDirectory(folder);

        if (!ExtractOne("ClassroomKit.Agent.g.resources", RegularResource, Path.Combine(folder, "HarmonyOS_SansSC_Regular.ttf")))
            return null;
        if (!ExtractOne("ClassroomKit.Agent.g.resources", SemiboldResource, Path.Combine(folder, "HarmonyOS_SansSC_Semibold.ttf")))
            return null;

        return folder + Path.DirectorySeparatorChar;
    }

    private static bool ExtractOne(string resourceName, string key, string destination)
    {
        try
        {
            var info = new FileInfo(destination);
            // WPF 抽出来的 ttf 与内嵌资源等长；长度对上就不再重写
            using var stream = typeof(FontCatalog).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return false;

            using var reader = new ResourceReader(stream);
            foreach (System.Collections.DictionaryEntry entry in reader)
            {
                if (entry.Key is not string name || !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Value is not Stream payload) return false;
                if (info.Exists && info.Length == payload.Length) return true;

                using var target = File.Create(destination);
                payload.CopyTo(target);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
