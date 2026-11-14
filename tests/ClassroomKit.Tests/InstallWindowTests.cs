using ClassroomKit.Core.Update;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>自动升级时间窗（纯逻辑，不需要 Windows）。</summary>
internal static class InstallWindowTests
{
    public static async Task RunAsync()
    {
        await Run("显式时间窗解析（默认不限，只有用户填了才限制）", () =>
        {
            True(InstallWindow.TryParse("02:00-05:00", out var start, out var end), "应能解析默认窗口");
            Eq(new TimeOnly(2, 0), start, "开始");
            Eq(new TimeOnly(5, 0), end, "结束");
        });

        await Run("窗口内/外判定", () =>
        {
            Eq("02:00-05:00", InstallWindow.Normalize("02:00-05:00"));
            True(InstallWindow.IsOpen("02:00-05:00", new TimeOnly(3, 0)), "凌晨 3 点应在窗口内");
            True(InstallWindow.IsOpen("02:00-05:00", new TimeOnly(2, 0)), "起点算窗口内");
            False(InstallWindow.IsOpen("02:00-05:00", new TimeOnly(5, 0)), "终点不算窗口内");
            False(InstallWindow.IsOpen("02:00-05:00", new TimeOnly(13, 0)), "下午不该装");
        });

        await Run("跨午夜窗口 23:00-06:00", () =>
        {
            True(InstallWindow.IsOpen("23:00-06:00", new TimeOnly(23, 30)), "23:30 在窗口内");
            True(InstallWindow.IsOpen("23:00-06:00", new TimeOnly(5, 59)), "5:59 在窗口内");
            False(InstallWindow.IsOpen("23:00-06:00", new TimeOnly(12, 0)), "中午不在窗口内");
            Contains(InstallWindow.Describe("23:00-06:00"), "跨午夜");
        });

        await Run("空值与乱写按不限时间处理", () =>
        {
            True(InstallWindow.IsOpen("", new TimeOnly(10, 0)), "空字符串 = 不限");
            True(InstallWindow.IsOpen("sometime", new TimeOnly(10, 0)), "格式错 = 不限（别把机器卡死在不升级）");
            Eq("不限时间", InstallWindow.Describe(""));
            Eq("", InstallWindow.Normalize(""), "空串要保留为不限时间，不能偷偷塞回默认窗口");
        });

        await Run("宽松写法与规范输出", () =>
        {
            Eq("02:00-05:00", InstallWindow.Normalize("2:00~5:00"), "波浪号与单位数小时");
            Eq("23:00-06:00", InstallWindow.Normalize("23:00 至 06:00"), "中文「至」");
            Eq("01:30-04:00", InstallWindow.Normalize("01:30:00-04:00:00"), "带秒");
            Eq("", InstallWindow.Normalize("坏数据"), "写歪了按不限处理，别把机器卡死在不升级");
            Eq("", InstallWindow.Default, "默认值必须是不限时间");
        });

        await Run("起止相同等于不限", () =>
        {
            True(InstallWindow.IsOpen("03:00-03:00", new TimeOnly(15, 0)), "起止相同 = 全天不限");
        });
    }
}
