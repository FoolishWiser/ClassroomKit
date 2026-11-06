using System.Text.RegularExpressions;

namespace ClassroomKit.Core.Board;

public enum BoardKind
{
    Unknown = 0,
    /// <summary>ClassInX 板书窗（全屏）。</summary>
    ClassInBoard,
    /// <summary>ClassInX 的置顶小悬浮窗。</summary>
    ClassInFloating,
    /// <summary>希沃白板主窗（全屏）。</summary>
    SeewoBoard,
    /// <summary>希沃白板的置顶小悬浮窗。</summary>
    SeewoFloating,
    /// <summary>WPS 演示编辑窗口。</summary>
    WpsEditor,
    /// <summary>WPS 演示放映窗口（全屏、不置顶）。</summary>
    WpsShow,
    /// <summary>其它被当作"全屏应用"的窗口（可配置进程名）。</summary>
    OtherFullscreen
}

/// <summary>
/// 按"进程名 + 窗口类名 + 标题 + 矩形"把窗口归类。
/// 不写死安装路径与版本号：WPS 装在 %LOCALAPPDATA% 也能认出来。
/// </summary>
public static class WindowClassifier
{
    // WPS 12 实测类名 PP12FrameClass；老版本是 PPFrameClass / PP10FrameClass
    private static readonly Regex WpsEditorClass = new(@"^PP\d*FrameClass$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 放映窗口标题实测：WPS Presentation Slide Show - [演示文稿2]
    private static readonly string[] WpsShowTitlePrefixes =
    {
        "WPS Presentation Slide Show",
        "WPS 演示放映",
        "WPS演示放映"
    };

    public static bool IsClassIn(WindowInfo w) => Contains(w.ProcessName, "classin");

    public static bool IsSeewo(WindowInfo w) =>
        Contains(w.ProcessName, "easinote") || Contains(w.ProcessName, "seewo") || Contains(w.ProcessName, "hitevision") || Contains(w.ProcessName, "hhbs");

    public static bool IsWpsProcess(WindowInfo w) =>
        Contains(w.ProcessName, "wpp") || Contains(w.ProcessName, "wps") || Contains(w.ProcessName, "et");

    /// <summary>进程是否来自 WPS 安装目录（Kingsoft\WPS Office）。拿不到路径时按进程名兜底。</summary>
    public static bool IsWpsInstalled(WindowInfo w)
    {
        if (!IsWpsProcess(w)) return false;
        if (string.IsNullOrWhiteSpace(w.ProcessPath)) return true;
        return w.ProcessPath.Contains(@"\Kingsoft\WPS Office\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>放映态：标题前缀命中（首选），或 wpp 进程 + 全屏 + 标题含 Slide Show（多语言兜底）。</summary>
    public static bool LooksLikeWpsShow(WindowInfo w, int screenWidth, int screenHeight)
    {
        if (!w.Visible) return false;
        foreach (var prefix in WpsShowTitlePrefixes)
        {
            if (w.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return w.CoversScreen(screenWidth, screenHeight);
        }

        if (Contains(w.ProcessName, "wpp") && w.CoversScreen(screenWidth, screenHeight)
            && w.Title.Contains("slide show", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool LooksLikeWpsEditor(WindowInfo w)
    {
        if (!w.Visible) return false;
        if (WpsEditorClass.IsMatch(w.ClassName)) return true;
        return IsWpsInstalled(w) && w.Title.EndsWith("- WPS Office", StringComparison.OrdinalIgnoreCase);
    }

    public static BoardKind Classify(WindowInfo w, int screenWidth, int screenHeight, IReadOnlyCollection<string>? extraFullscreen = null)
    {
        if (!w.Visible) return BoardKind.Unknown;

        if (LooksLikeWpsShow(w, screenWidth, screenHeight)) return BoardKind.WpsShow;
        if (IsWpsProcess(w) && Contains(w.ProcessName, "wpp") && LooksLikeWpsEditor(w)) return BoardKind.WpsEditor;
        if (LooksLikeWpsEditor(w)) return BoardKind.WpsEditor;

        if (IsClassIn(w))
        {
            if (w.CoversScreen(screenWidth, screenHeight)) return BoardKind.ClassInBoard;
            if (w.IsTopmost && w.LooksFloating(screenWidth, screenHeight)) return BoardKind.ClassInFloating;
            return BoardKind.Unknown;
        }

        if (IsSeewo(w))
        {
            if (w.CoversScreen(screenWidth, screenHeight)) return BoardKind.SeewoBoard;
            if (w.IsTopmost && w.LooksFloating(screenWidth, screenHeight)) return BoardKind.SeewoFloating;
            return BoardKind.Unknown;
        }

        if (extraFullscreen is { Count: > 0 })
        {
            foreach (var name in extraFullscreen)
            {
                if (!string.IsNullOrWhiteSpace(name) && Contains(w.ProcessName, name.Trim()))
                {
                    if (w.CoversScreen(screenWidth, screenHeight)) return BoardKind.OtherFullscreen;
                }
            }
        }

        return BoardKind.Unknown;
    }

    /// <summary>把整屏窗口分成「情境」——冲突判定的输入。</summary>
    public static BoardSituation Situate(IEnumerable<WindowInfo> windows, int screenWidth, int screenHeight, IReadOnlyCollection<string>? extraFullscreen = null)
    {
        var s = new BoardSituation();
        foreach (var w in windows)
        {
            switch (Classify(w, screenWidth, screenHeight, extraFullscreen))
            {
                case BoardKind.ClassInBoard: s.ClassInFullscreen = true; break;
                case BoardKind.ClassInFloating: s.ClassInFloating = true; break;
                case BoardKind.SeewoBoard: s.SeewoFullscreen = true; break;
                case BoardKind.SeewoFloating: s.SeewoFloating = true; break;
                case BoardKind.WpsShow: s.WpsShow = true; break;
                case BoardKind.OtherFullscreen: s.OtherFullscreen = true; break;
            }
        }
        return s;
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>当前桌面上跟"板书软件冲突"有关的状态。</summary>
public sealed class BoardSituation
{
    public bool ClassInFloating { get; set; }
    public bool SeewoFloating { get; set; }
    public bool ClassInFullscreen { get; set; }
    public bool SeewoFullscreen { get; set; }
    public bool WpsShow { get; set; }
    public bool OtherFullscreen { get; set; }

    public bool BothFloating => ClassInFloating && SeewoFloating;

    public bool AnyFullscreen => ClassInFullscreen || SeewoFullscreen || WpsShow || OtherFullscreen;

    /// <summary>是否该弹窗：两个悬浮窗共存 + 进入任一全屏场景。</summary>
    public bool ShouldPrompt => BothFloating && AnyFullscreen;

    public string Describe()
    {
        var parts = new List<string>();
        if (ClassInFloating) parts.Add("ClassIn悬浮窗");
        if (SeewoFloating) parts.Add("希沃悬浮窗");
        if (ClassInFullscreen) parts.Add("ClassIn全屏");
        if (SeewoFullscreen) parts.Add("希沃全屏");
        if (WpsShow) parts.Add("WPS放映");
        if (OtherFullscreen) parts.Add("其它全屏");
        return parts.Count == 0 ? "无" : string.Join(" + ", parts);
    }
}
