using ClassroomKit.Core.Board;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>配方坐标缩放：学校一体机 150% 缩放下对话框是 1230×672，必须按 1.5 折算。</summary>
internal static class RecipeTests
{
    public static async Task RunAsync()
    {
        await Run("配方缩放", () =>
        {
            var recipe = UiRecipe.CreateDefault();

            var (sx, sy) = recipe.ScaleFor(820, 448);
            Eq(1.0, sx, "96 DPI 对话框 x 不缩放");
            Eq(1.0, sy, "96 DPI 对话框 y 不缩放");

            (sx, sy) = recipe.ScaleFor(1230, 672);
            Eq(1.5, sx, "150% 缩放 x=1.5");
            Eq(1.5, sy, "150% 缩放 y=1.5");
            var (w, h) = recipe.ExpectedPanel(1230, 672);
            Eq(510, w, "150% 下方面板宽 510");
            Eq(417, h, "150% 下方面板高 417");
            Eq(1000, (int)Math.Round(recipe.SaveLocal.SaveLocalX * sx), "150% 下「保存至本地」x=1000");
            Eq(117, (int)Math.Round(recipe.SaveLocal.SaveLocalY * sy), "150% 下「保存至本地」y=117");

            (sx, sy) = recipe.ScaleFor(1025, 560); // 125%
            Eq(1.25, sx, "125% 缩放 x=1.25");
            Eq(1.25, sy, "125% 缩放 y=1.25");

            (sx, sy) = recipe.ScaleFor(0, 0);
            Eq(1.0, sx, "尺寸异常时不缩放");
            (w, h) = recipe.ExpectedPanel(0, 0);
            Eq(340, w, "异常尺寸下方面板按配方 340");
            Eq(278, h, "异常尺寸下方面板按配方 278");
            return Task.CompletedTask;
        });
    }
}
