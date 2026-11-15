using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextAlignment = System.Windows.TextAlignment;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ClassroomKit.Agent.Desktop.Controls;

/// <summary>
/// 带 −/+ 的数字输入框：编辑过程中允许空串，失焦/回车才提交并按范围夹紧，
/// 避免 WPF 把「清空重输」的中间态当成非法值直接弹红框。
/// </summary>
public sealed class NumericInput : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericInput),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumericInput), new PropertyMetadata(1));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumericInput), new PropertyMetadata(32));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumericInput), new PropertyMetadata(1));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumericInput), new PropertyMetadata(""));

    private readonly TextBox _box;
    private bool _syncing;

    public NumericInput()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chrome = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            MinHeight = 34
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        chrome.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        SetColumnSpan(chrome, 4);

        var inner = new Grid();
        chrome.Child = inner;
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var minus = MakeButton("\uE738", "减少");
        minus.Click += (_, _) => Bump(-1);

        _box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13,
            MinHeight = 28,
            Padding = new Thickness(0)
        };
        _box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        _box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
        _box.LostFocus += (_, _) => Commit();
        _box.PreviewKeyDown += OnBoxKeyDown;
        _box.TextChanged += OnBoxTextChanged;

        var plus = MakeButton("\uE710", "增加");
        plus.Click += (_, _) => Bump(1);

        var suffix = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
            FontSize = 12,
            IsHitTestVisible = false
        };
        suffix.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        suffix.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Suffix))
        {
            Source = this
        });

        inner.Children.Add(minus);
        inner.Children.Add(_box);
        inner.Children.Add(plus);
        inner.Children.Add(suffix);
        SetColumn(minus, 0);
        SetColumn(_box, 1);
        SetColumn(plus, 2);
        SetColumn(suffix, 3);

        Children.Add(chrome);
        UpdateText();
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericInput input && !input._syncing) input.UpdateText();
    }

    private Button MakeButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        return button;
    }

    private void Bump(int direction)
    {
        var next = Math.Clamp(Value + direction * Math.Max(1, Step), Minimum, Maximum);
        if (next == Value) return;
        SetCurrentValue(ValueProperty, next);
        UpdateText();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
                break;
            case Key.Escape:
                UpdateText();
                e.Handled = true;
                break;
            case Key.Up:
                Bump(1);
                e.Handled = true;
                break;
            case Key.Down:
                Bump(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // 只允许数字：输入法/粘贴进来的其它字符直接挡掉，但保留空串这一中间态
        var cleaned = new string(_box.Text.Where(char.IsDigit).ToArray());
        if (cleaned == _box.Text) return;
        var caret = _box.CaretIndex;
        _box.Text = cleaned;
        _box.CaretIndex = Math.Min(caret, cleaned.Length);
    }

    private void Commit()
    {
        if (_syncing) return;
        if (!int.TryParse(_box.Text, out var parsed))
        {
            UpdateText();
            return;
        }
        var clamped = Math.Clamp(parsed, Minimum, Maximum);
        if (clamped != Value) SetCurrentValue(ValueProperty, clamped);
        UpdateText();
    }

    private void UpdateText()
    {
        _syncing = true;
        try
        {
            _box.Text = Value.ToString();
        }
        finally
        {
            _syncing = false;
        }
    }
}
