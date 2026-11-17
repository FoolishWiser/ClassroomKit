namespace ClassroomKit.Core.Board;

using ClassroomKit.Core.Model;

/// <summary>习惯策略给出的动作。</summary>
public enum HabitAction
{
    /// <summary>不需要额外干预，按原来的冷却规则决定是否弹窗。</summary>
    None = 0,
    /// <summary>照常弹冲突窗，交老师处理。</summary>
    Prompt,
    /// <summary>不弹窗，静默跳过这一拍。</summary>
    SuppressPrompt,
    /// <summary>不弹窗，直接关掉正在使用的竞争软件。</summary>
    CloseCompetitors
}

/// <summary>一次习惯判定的结果。Close 里是要关掉的软件（只在 CloseCompetitors 时有值）。</summary>
public sealed record HabitDecision(
    HabitAction Action,
    HabitApp Preferred,
    IReadOnlyList<HabitApp> Close,
    string Reason)
{
    public static HabitDecision Of(HabitAction action, HabitApp preferred, string reason,
        IReadOnlyList<HabitApp>? close = null) =>
        new(action, preferred, close ?? Array.Empty<HabitApp>(), reason);
}

/// <summary>
/// 按科目的「老师习惯」判定冲突时怎么办。纯函数，桌面与守护线程都能调，方便单测。
/// 关键约束（用户 2026-11-17 补充）：
/// 只在「老师刚打开习惯软件」这个方向替他让路；如果老师先开着习惯软件、又手动打开了另一款，
/// 那是他自己的选择，既不关也不弹窗。
/// </summary>
public static class SubjectHabitPolicy
{
    /// <summary>按科目找规则（忽略大小写与首尾空白）。没启用或没配到返回 null。</summary>
    public static HabitRule? Resolve(HabitsConfig? cfg, string? subject)
    {
        if (cfg is null) return null;
        var key = (subject ?? "").Trim();
        if (key.Length == 0) return null;

        HabitRule? loose = null;
        foreach (var rule in cfg.Rules ?? new List<HabitRule>())
        {
            if (rule is null) continue;
            var name = (rule.Subject ?? "").Trim();
            if (name.Length == 0) continue;
            if (string.Equals(name, key, StringComparison.Ordinal)) return rule;
            if (loose is null && string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) loose = rule;
        }
        return loose;
    }

    /// <summary>该软件在当前情境里是否正在被使用。</summary>
    public static bool IsActive(HabitApp app, BoardSituation? situation)
    {
        if (situation is null) return false;
        return app switch
        {
            HabitApp.ClassIn => situation.HasClassIn,
            HabitApp.Seewo => situation.HasSeewo,
            HabitApp.Wps => situation.WpsActive,
            _ => false
        };
    }

    /// <summary>当前情境里正在使用、且与习惯软件冲突的其它软件。</summary>
    public static IReadOnlyList<HabitApp> ActiveCompetitors(HabitApp app, BoardSituation? situation)
    {
        if (situation is null) return Array.Empty<HabitApp>();
        var list = new List<HabitApp>(2);
        switch (app)
        {
            case HabitApp.ClassIn:
                if (situation.HasSeewo) list.Add(HabitApp.Seewo);
                if (situation.WpsActive) list.Add(HabitApp.Wps);
                break;
            case HabitApp.Seewo:
                if (situation.HasClassIn) list.Add(HabitApp.ClassIn);
                if (situation.WpsActive) list.Add(HabitApp.Wps);
                break;
            case HabitApp.Wps:
                if (situation.HasClassIn) list.Add(HabitApp.ClassIn);
                if (situation.HasSeewo) list.Add(HabitApp.Seewo);
                break;
        }
        return list;
    }

    /// <summary>
    /// 判定这一拍该怎么做。current 是本拍情境，previous 是上一拍（可为 null），
    /// firstSample 表示守护刚启动、还没有可比的上一拍。
    /// </summary>
    public static HabitDecision Decide(HabitsConfig? cfg, string? subject,
        BoardSituation? current, BoardSituation? previous, bool firstSample)
    {
        if (cfg is null || !cfg.Enabled)
            return HabitDecision.Of(HabitAction.Prompt, HabitApp.None, "习惯模式没开启");

        var rule = Resolve(cfg, subject);
        if (rule is null)
            return HabitDecision.Of(HabitAction.SuppressPrompt, HabitApp.None,
                "习惯模式已开启，但当前科目没有设置：不做任何处理");

        var app = HabitApps.Parse(rule.App);
        if (app == HabitApp.None)
            return HabitDecision.Of(HabitAction.SuppressPrompt, HabitApp.None,
                "习惯软件没选，不做任何处理");

        var label = HabitApps.Label(app);
        var subjectText = (subject ?? "").Trim();

        if (rule.Understand)
            return HabitDecision.Of(HabitAction.Prompt, app,
                subjectText + " 习惯 " + label + "，理解：冲突交老师");

        if (current is null)
            return HabitDecision.Of(HabitAction.None, app, "还没取到桌面情境");

        if (firstSample)
            return HabitDecision.Of(HabitAction.SuppressPrompt, app,
                "刚启动，先观察一拍，不替老师动手");

        var active = IsActive(app, current);
        if (!active)
        {
            return current.ShouldPrompt
                ? HabitDecision.Of(HabitAction.SuppressPrompt, app,
                    "老师没在用 " + label + "，不弹冲突窗")
                : HabitDecision.Of(HabitAction.None, app, "老师没在用 " + label);
        }

        var close = ActiveCompetitors(app, current);
        var wasActive = IsActive(app, previous);

        if (!wasActive)
        {
            // 老师刚打开习惯软件。若此时另一个软件已经在用，替他让路。
            if (close.Count == 0)
                return HabitDecision.Of(HabitAction.None, app, label + " 已就绪，没有竞争软件");

            var hadCompetitor = ActiveCompetitors(app, previous).Count > 0;
            if (!hadCompetitor)
                return HabitDecision.Of(HabitAction.SuppressPrompt, app,
                    label + " 和其它软件同时出现，先不处理");

            return HabitDecision.Of(HabitAction.CloseCompetitors, app,
                "老师打开 " + label + "，静默关闭 " + string.Join("、", close.Select(HabitApps.Label)),
                close);
        }

        // 习惯软件一直在用，老师又自己打开了别的：不关、不弹。
        return current.ShouldPrompt
            ? HabitDecision.Of(HabitAction.SuppressPrompt, app,
                "老师自己在 " + label + " 之上又开了别的，不处理")
            : HabitDecision.Of(HabitAction.None, app, "老师一直在用 " + label);
    }
}
