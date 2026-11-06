using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassroomKit.Core.Board;

/// <summary>
/// ClassInX 板书保存流程的"界面配方"：窗口尺寸 + 相对坐标。
/// 单独存盘（%ProgramData%\ClassroomKit\ui-recipe.json），界面变了只要重新探测，不用改代码。
/// 所有坐标都是相对窗口左上角的偏移。
/// </summary>
public sealed class UiRecipe
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ProbedAt { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "default";
    public SaveLocalRecipe SaveLocal { get; set; } = new();
    public OverwriteRecipe Overwrite { get; set; } = new();

    /// <summary>窗口尺寸容差（DPI 缩放下尺寸会变，用比例匹配更稳）。</summary>
    [JsonIgnore]
    public double SizeTolerance { get; set; } = 0.06;

    public sealed class SaveLocalRecipe
    {
        /// <summary>「保存和分享板书」窗口。</summary>
        public int ShareWidth { get; set; } = 820;
        public int ShareHeight { get; set; } = 448;
        /// <summary>该窗口里「保存至本地」图标的位置。</summary>
        public int SaveLocalX { get; set; } = 667;
        public int SaveLocalY { get; set; } = 78;

        /// <summary>「保存至本地」面板。</summary>
        public int PanelWidth { get; set; } = 340;
        public int PanelHeight { get; set; } = 278;

        public int CheckboxY { get; set; } = 211;
        public int EdbX { get; set; } = 25;
        public int PngX { get; set; } = 92;
        public int PdfX { get; set; } = 159;

        public int SaveButtonX { get; set; } = 125;
        public int SaveButtonY { get; set; } = 258;
    }

    public sealed class OverwriteRecipe
    {
        /// <summary>重复保存时弹出的「…已经存在，是否替换它？」。</summary>
        public int Width { get; set; } = 360;
        public int Height { get; set; } = 212;
        public int YesX { get; set; } = 195;
        public int YesY { get; set; } = 174;
    }

    public bool MatchesShare(int width, int height) =>
        Near(SaveLocal.ShareWidth, width) && Near(SaveLocal.ShareHeight, height);

    public bool MatchesPanel(int width, int height) =>
        Near(SaveLocal.PanelWidth, width) && Near(SaveLocal.PanelHeight, height);

    public bool MatchesOverwrite(int width, int height) =>
        Near(Overwrite.Width, width) && Near(Overwrite.Height, height);

    private bool Near(int expected, int actual)
    {
        if (expected <= 0) return false;
        var diff = Math.Abs(expected - actual);
        return diff <= Math.Max(8, expected * SizeTolerance);
    }

    public static UiRecipe CreateDefault() => new();

    public static UiRecipe Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var recipe = JsonSerializer.Deserialize<UiRecipe>(json);
                if (recipe is not null && recipe.SaveLocal.PanelWidth > 0) return recipe;
            }
        }
        catch
        {
            // 配置坏了就用默认值，不要让面板/托盘起不来
        }
        return CreateDefault();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
