using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SETBoard.Board;

namespace SETBoard;

/// <summary>对话框里的一条函数：Latex 为原始输入，Expression 为转换后的求值表达式。</summary>
public sealed record FunctionCurveSpec(
    string Latex,
    string Expression,
    double ParameterA,
    uint Color);

/// <summary>函数图像对话框要回传的结果：一个坐标系里可以有多条不同颜色的曲线。</summary>
public sealed record FunctionSpec(
    IReadOnlyList<FunctionCurveSpec> Curves,
    double XMin,
    double XMax,
    double YMin,
    double YMax);

/// <summary>
/// 绘制函数对话框：在一个坐标系里管理多条曲线，每条曲线有独立颜色与参数 a。
/// 自带预设与数学键盘，触屏上不用依赖系统输入法。
/// </summary>
internal sealed class FunctionDialog : Window
{
    private static readonly SolidColorBrush SurfaceBrush = new(Color.Parse("#16252F"));
    private static readonly SolidColorBrush FieldBrush = new(Color.Parse("#0F1E27"));
    private static readonly SolidColorBrush KeyBrush = new(Color.Parse("#1C2C37"));
    private static readonly SolidColorBrush LineBrush = new(Color.Parse("#33424D"));
    private static readonly SolidColorBrush TextBrush = new(Color.Parse("#E8EFF4"));
    private static readonly SolidColorBrush MutedBrush = new(Color.Parse("#8FA6B4"));
    private static readonly SolidColorBrush AccentBrush = new(Color.Parse("#FFD54A"));
    private static readonly SolidColorBrush ChipActiveBrush = new(Color.Parse("#233A48"));

    private readonly TextBox _expressionBox;
    private readonly TextBox _xMinBox;
    private readonly TextBox _xMaxBox;
    private readonly TextBlock _paramLabel;
    private readonly TextBlock _errorText;
    private readonly LatexDisplay _formula;
    private readonly FunctionPreview _preview;
    private readonly Slider _slider;
    private readonly StackPanel _chipPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly List<TextBlock> _chipTexts = new();
    private readonly uint _color;
    private readonly double _strokeWidth;
    private readonly List<FunctionCurveSpec> _drafts = new();

    private int _selected;
    private bool _syncing;
    private double _yMin = -4;
    private double _yMax = 4;

    public FunctionSpec? Result { get; private set; }

    public FunctionDialog(FunctionItem? editing, uint color, double strokeWidth)
    {
        _color = color;
        _strokeWidth = Math.Clamp(strokeWidth, 1.5, 6);

        Title = editing is null ? "绘制函数" : "编辑函数";
        MinWidth = 480;
        Width = 620;
        Height = 720;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0C1B24"));
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // 编辑已有图像时把它所有的曲线读进来；旧存档只有一条曲线也能兼容
        if (editing is not null)
        {
            foreach (var curve in editing.GetCurves())
            {
                _drafts.Add(new FunctionCurveSpec(
                    string.IsNullOrWhiteSpace(curve.Latex) ? curve.Expression : curve.Latex,
                    curve.Expression,
                    curve.ParameterA,
                    curve.Color));
            }
        }

        if (_drafts.Count == 0) _drafts.Add(CreateDraft("\\sin(a x)", "sin(a x)", 1));

        _expressionBox = new TextBox
        {
            Text = _drafts[0].Latex,
            PlaceholderText = "例如 \\frac{1}{1+x^2}、\\sin(a x)、\\sqrt{x}",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 18,
            Height = 46,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = LineBrush,
            CornerRadius = new CornerRadius(10),
        };
        _expressionBox.TextChanged += (_, _) => OnExpressionChanged();

        _formula = new LatexDisplay
        {
            FormulaFontSize = 28,
            Latex = _drafts[0].Latex,
        };

        _xMinBox = CreateRangeBox(editing?.XMin ?? -10);
        _xMaxBox = CreateRangeBox(editing?.XMax ?? 10);
        _xMinBox.TextChanged += (_, _) => OnRangeChanged();
        _xMaxBox.TextChanged += (_, _) => OnRangeChanged();

        _paramLabel = new TextBlock
        {
            Text = $"a = {_drafts[0].ParameterA:0.0}",
            Foreground = TextBrush,
            FontSize = 13,
            Width = 62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };

        _preview = new FunctionPreview(color, _strokeWidth);

        var slider = new Slider
        {
            Minimum = -5,
            Maximum = 5,
            Value = _drafts[0].ParameterA,
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            Width = 300,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider = slider;
        slider.ValueChanged += (_, e) =>
        {
            if (_syncing || _selected < 0 || _selected >= _drafts.Count) return;
            _paramLabel.Text = $"a = {e.NewValue:0.0}";
            _drafts[_selected] = _drafts[_selected] with { ParameterA = e.NewValue };
            RefreshPreview();
        };

        _errorText = new TextBlock
        {
            IsVisible = false,
            Foreground = new SolidColorBrush(Color.Parse("#FF9B9B")),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };

        Content = BuildContent(slider, editing is null);
        RebuildChips();
        Opened += (_, _) =>
        {
            OnExpressionChanged();
            _expressionBox.Focus();
            _expressionBox.CaretIndex = _expressionBox.Text?.Length ?? 0;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        UiaTopmost.MakeTopmost(this);
        WindowFx.FadeIn(this);
    }

    private static TextBox CreateRangeBox(double value) => new()
    {
        Text = value.ToString("0.###", CultureInfo.InvariantCulture),
        Width = 68,
        Height = 32,
        FontSize = 13,
        Padding = new Thickness(8, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Background = FieldBrush,
        Foreground = TextBrush,
        BorderBrush = LineBrush,
        CornerRadius = new CornerRadius(8),
    };

    // ---------------- 曲线列表 ----------------

    /// <summary>按顺序取还没用过的颜色，保证同一坐标系里几条曲线颜色互不相同。</summary>
    private uint PickColor()
    {
        uint[] palette =
        {
            _color, 0xFF4DABF7, 0xFFFF6B6B, 0xFF51CF66, 0xFFFFD54A, 0xFFB197FC, 0xFFFF922B,
        };

        foreach (uint candidate in palette)
        {
            if (!_drafts.Any(draft => draft.Color == candidate)) return candidate;
        }

        return palette[_drafts.Count % palette.Length];
    }

    private FunctionCurveSpec CreateDraft(string latex, string expression, double parameterA) =>
        new(latex, expression, parameterA, PickColor());

    private void RebuildChips()
    {
        _chipPanel.Children.Clear();
        _chipTexts.Clear();

        for (int i = 0; i < _drafts.Count; i++)
        {
            int index = i;
            var text = new TextBlock
            {
                Text = ChipText(_drafts[i]),
                FontSize = 12.5,
                Foreground = i == _selected ? TextBrush : MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 96,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _chipTexts.Add(text);

            var dot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(ToColor(_drafts[i].Color)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(dot);
            content.Children.Add(text);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
            };
            row.Children.Add(content);

            if (_drafts.Count > 1)
            {
                var remove = new Button
                {
                    Content = "✕",
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = MutedBrush,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                remove.Click += (_, e) =>
                {
                    RemoveDraft(index);
                    e.Handled = true;
                };
                row.Children.Add(remove);
            }

            var chip = new Border
            {
                Padding = new Thickness(10, 5, 8, 5),
                CornerRadius = new CornerRadius(10),
                Background = i == _selected ? ChipActiveBrush : SurfaceBrush,
                BorderBrush = i == _selected ? AccentBrush : LineBrush,
                BorderThickness = new Thickness(1),
                Child = row,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            chip.PointerPressed += (_, _) => SelectDraft(index);
            _chipPanel.Children.Add(chip);
        }
    }

    private static string ChipText(FunctionCurveSpec draft)
    {
        string text = string.IsNullOrWhiteSpace(draft.Expression) ? draft.Latex : draft.Expression;
        return text.Replace(" ", string.Empty);
    }

    private void UpdateChipText(int index)
    {
        if (index >= 0 && index < _chipTexts.Count) _chipTexts[index].Text = ChipText(_drafts[index]);
    }

    private void SelectDraft(int index)
    {
        if (index == _selected) return;
        _selected = index;
        LoadDraft();
    }

    /// <summary>把选中的曲线回填到编辑器。</summary>
    private void LoadDraft()
    {
        var draft = _drafts[_selected];
        _syncing = true;
        _expressionBox.Text = draft.Latex;
        _slider.Value = draft.ParameterA;
        _paramLabel.Text = $"a = {draft.ParameterA:0.0}";
        _syncing = false;

        _formula.Latex = draft.Latex;
        _errorText.IsVisible = false;
        RebuildChips();
        RecalculateYRange();
        RefreshPreview();

        _expressionBox.Focus();
        _expressionBox.CaretIndex = _expressionBox.Text?.Length ?? 0;
    }

    private void AddDraft()
    {
        _drafts.Add(CreateDraft("\\sin(a x)", "sin(a x)", 1));
        _selected = _drafts.Count - 1;
        LoadDraft();
    }

    private void RemoveDraft(int index)
    {
        if (_drafts.Count <= 1) return;
        _drafts.RemoveAt(index);
        if (index < _selected) _selected--;
        if (_selected >= _drafts.Count) _selected = _drafts.Count - 1;
        LoadDraft();
    }

    // ---------------- 界面 ----------------

    private Control BuildContent(Slider slider, bool isNew)
    {
        var root = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 20), Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = isNew ? "绘制函数" : "编辑函数",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "一个坐标系里可以叠加多条曲线，颜色自动区分；参数 a 用滑块调节，切换曲线后分别调整。",
            FontSize = 12.5,
            Foreground = MutedBrush,
            Margin = new Thickness(0, -6, 0, 0),
        });

        // 曲线切换条
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "坐标轴上的函数",
            FontSize = 12.5,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var addButton = new Button
        {
            Content = "＋ 新建函数",
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(9),
            Background = KeyBrush,
            Foreground = TextBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            FontSize = 12.5,
        };
        addButton.Click += (_, _) => AddDraft();
        Grid.SetColumn(addButton, 1);
        header.Children.Add(addButton);
        panel.Children.Add(header);

        var chipScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 32,
            Content = _chipPanel,
        };
        panel.Children.Add(chipScroll);

        // 预设
        var presetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        presetRow.Children.Add(new TextBlock
        {
            Text = "预设",
            FontSize = 12.5,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        foreach (var (label, latex, parameterA) in Presets)
        {
            var button = CreatePresetButton(label);
            button.Click += (_, _) => ApplyPreset(latex, parameterA);
            presetRow.Children.Add(button);
        }

        panel.Children.Add(presetRow);

        var expressionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        var prefix = new TextBlock
        {
            Text = "f(x) =",
            FontSize = 17,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        Grid.SetColumn(_expressionBox, 1);
        expressionRow.Children.Add(prefix);
        expressionRow.Children.Add(_expressionBox);
        panel.Children.Add(expressionRow);

        // LaTeX 排版显示：分数线、根号、上下标按数学格式渲染
        var formulaBorder = new Border
        {
            Height = 50,
            CornerRadius = new CornerRadius(12),
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _formula,
        };
        panel.Children.Add(formulaBorder);

        var previewBorder = new Border
        {
            Height = 160,
            CornerRadius = new CornerRadius(12),
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _preview,
        };
        panel.Children.Add(previewBorder);

        var rangeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rangeRow.Children.Add(new TextBlock
        {
            Text = "横轴",
            FontSize = 13,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        });
        rangeRow.Children.Add(_xMinBox);
        rangeRow.Children.Add(new TextBlock
        {
            Text = "—",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        rangeRow.Children.Add(_xMaxBox);
        panel.Children.Add(rangeRow);

        var paramRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        paramRow.Children.Add(new TextBlock
        {
            Text = "参数 a",
            FontSize = 13,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        paramRow.Children.Add(slider);
        paramRow.Children.Add(_paramLabel);
        panel.Children.Add(paramRow);

        panel.Children.Add(BuildKeypad());
        panel.Children.Add(_errorText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var cancel = new Button
        {
            Content = "取消",
            Height = 42,
            Padding = new Thickness(22, 0, 22, 0),
            CornerRadius = new CornerRadius(10),
            Background = KeyBrush,
            Foreground = TextBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            FontSize = 14,
        };
        cancel.Click += (_, _) => Close(null);

        var confirm = new Button
        {
            Content = isNew ? "插入图像" : "保存修改",
            Height = 42,
            Padding = new Thickness(24, 0, 24, 0),
            CornerRadius = new CornerRadius(10),
            Background = AccentBrush,
            Foreground = new SolidColorBrush(Color.Parse("#141A10")),
            BorderThickness = new Thickness(0),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
        };
        confirm.Click += (_, _) => Confirm();

        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);

        root.Content = panel;
        return root;
    }

    private static readonly (string Label, string Latex, double ParameterA)[] Presets =
    {
        ("一次函数", "a x + 1", 1),
        ("二次函数", "a x^{2}", 1),
        ("幂函数", "x^{a}", 2),
        ("指数函数", "2^{x}", 1),
        ("对数函数", "\\ln(x)", 1),
    };

    private static Button CreatePresetButton(string label) => new()
    {
        Content = label,
        Height = 30,
        Padding = new Thickness(10, 0, 10, 0),
        CornerRadius = new CornerRadius(9),
        Background = KeyBrush,
        Foreground = TextBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        FontSize = 12.5,
    };

    /// <summary>预设只替换当前曲线的表达式，多条曲线可以分别套用不同预设。</summary>
    private void ApplyPreset(string latex, double parameterA)
    {
        _syncing = true;
        _expressionBox.Text = latex;
        _slider.Value = parameterA;
        _paramLabel.Text = $"a = {parameterA:0.0}";
        _syncing = false;

        _drafts[_selected] = _drafts[_selected] with { ParameterA = parameterA };
        OnExpressionChanged();
        _expressionBox.Focus();
        _expressionBox.CaretIndex = latex.Length;
    }

    /// <summary>数学键盘：大按钮、常用符号，触屏上也能准确点击。</summary>
    private Control BuildKeypad()
    {
        // 键盘直接插入 LaTeX 片段；Caret 为插入后光标相对插入起点的位置，-1 表示放到末尾
        var keys = new (string Label, string Insert, int Caret)[]
        {
            ("7", "7", -1), ("8", "8", -1), ("9", "9", -1), ("(", "(", -1), (")", ")", -1), ("÷", "\\div ", -1),
            ("4", "4", -1), ("5", "5", -1), ("6", "6", -1), ("x", "x", -1), ("a", "a", -1), ("×", "\\times ", -1),
            ("1", "1", -1), ("2", "2", -1), ("3", "3", -1), ("π", "\\pi ", -1), ("e", "e", -1), ("−", "-", -1),
            ("0", "0", -1), (".", ".", -1), ("^", "^{}", 2), ("√", "\\sqrt{}", 6),
            ("分式", "\\frac{}{}", 6), ("+", "+", -1),
            ("sin", "\\sin(", -1), ("cos", "\\cos(", -1), ("tan", "\\tan(", -1),
            ("ln", "\\ln(", -1), ("lg", "\\lg(", -1), ("⌫", BackspaceKey, -1),
            ("|x|", "\\left|x\\right|", -1), ("asin", "\\asin(", -1), ("acos", "\\acos(", -1),
            ("atan", "\\atan(", -1), ("清空", ClearKey, -1),
        };

        var grid = new UniformGrid
        {
            Columns = 6,
            Rows = 6,
        };

        foreach (var (label, insert, caret) in keys)
        {
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(3),
                Height = 42,
                FontSize = 15,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Background = KeyBrush,
                Foreground = TextBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => OnKey(insert, caret);
            grid.Children.Add(button);
        }

        return grid;
    }

    private const string BackspaceKey = "\u0008";
    private const string ClearKey = "\u0001";

    private void OnKey(string insert, int caretOffset = -1)
    {
        string current = _expressionBox.Text ?? string.Empty;
        int start = Math.Clamp(_expressionBox.SelectionStart, 0, current.Length);
        int end = Math.Clamp(_expressionBox.SelectionEnd, 0, current.Length);
        if (start > end) (start, end) = (end, start);

        if (insert == ClearKey)
        {
            _expressionBox.Text = string.Empty;
            _expressionBox.CaretIndex = 0;
        }
        else if (insert == BackspaceKey)
        {
            if (start == end && start > 0) start--;
            _expressionBox.Text = current[..start] + current[end..];
            _expressionBox.CaretIndex = start;
        }
        else
        {
            _expressionBox.Text = current[..start] + insert + current[end..];
            _expressionBox.CaretIndex = start + (caretOffset >= 0 ? caretOffset : insert.Length);
        }

        _expressionBox.Focus();
    }

    // ---------------- 数据同步 ----------------

    private void OnExpressionChanged()
    {
        if (_syncing || _selected < 0 || _selected >= _drafts.Count) return;

        string latex = _expressionBox.Text ?? string.Empty;
        _formula.Latex = latex;

        var current = _drafts[_selected];
        if (LatexMath.TryToPlain(latex, out string plain, out string? error)
            && MathExpression.TryParse(plain, out _, out error))
        {
            _errorText.IsVisible = false;
            _drafts[_selected] = current with { Latex = latex, Expression = plain };
        }
        else
        {
            _errorText.IsVisible = true;
            _errorText.Text = error ?? "表达式无法解析";
            _drafts[_selected] = current with { Latex = latex, Expression = string.Empty };
        }

        UpdateChipText(_selected);
        RecalculateYRange();
        RefreshPreview();
    }

    private void OnRangeChanged()
    {
        RecalculateYRange();
        RefreshPreview();
    }

    private (double Min, double Max) ReadXRange()
    {
        double min = ParseOrDefault(_xMinBox.Text, -10);
        double max = ParseOrDefault(_xMaxBox.Text, 10);
        if (max - min < 0.5) max = min + 1;
        return (min, max);
    }

    private static double ParseOrDefault(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value)
            ? value
            : fallback;

    /// <summary>只在表达式或横轴范围变化时重算纵轴，滑块调节时画面才稳定。</summary>
    private void RecalculateYRange()
    {
        var (min, max) = ReadXRange();
        var curves = _drafts
            .Where(draft => !string.IsNullOrWhiteSpace(draft.Expression))
            .Select(draft => (draft.Expression, draft.ParameterA));
        (_yMin, _yMax) = FunctionItem.AutoYRange(curves, min, max);
    }

    private void RefreshPreview()
    {
        var (min, max) = ReadXRange();
        var curves = _drafts
            .Where(draft => !string.IsNullOrWhiteSpace(draft.Expression))
            .Select(draft => new FunctionCurve
            {
                Expression = draft.Expression,
                ParameterA = draft.ParameterA,
                Color = draft.Color,
            })
            .ToList();

        _preview.SetCurves(curves, min, max, _yMin, _yMax);
    }

    private void Confirm()
    {
        if (_drafts.Any(draft => string.IsNullOrWhiteSpace(draft.Expression)))
        {
            _errorText.IsVisible = true;
            _errorText.Text = "有函数的表达式无法解析，请检查后再插入";
            return;
        }

        var (min, max) = ReadXRange();
        var curves = _drafts
            .Select(draft => draft with
            {
                Latex = string.IsNullOrWhiteSpace(draft.Latex) ? draft.Expression : draft.Latex,
            })
            .ToList();

        var spec = new FunctionSpec(curves, min, max, _yMin, _yMax);
        Result = spec;
        Close(spec);
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>对话框内的实时预览：复用 FunctionItem 的绘制逻辑，同时画多条曲线。</summary>
internal sealed class FunctionPreview : Control
{
    private readonly FunctionItem _item = new();
    private readonly uint _color;
    private readonly double _strokeWidth;
    private List<FunctionCurve> _curves = new();
    private double _xMin = -10;
    private double _xMax = 10;
    private double _yMin = -4;
    private double _yMax = 4;

    public FunctionPreview(uint color, double strokeWidth)
    {
        _color = color;
        _strokeWidth = strokeWidth;
    }

    public void SetCurves(List<FunctionCurve> curves, double xMin, double xMax, double yMin, double yMax)
    {
        _curves = curves;
        _xMin = xMin;
        _xMax = xMax;
        _yMin = yMin;
        _yMax = yMax;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_curves.Count == 0) return;

        double pad = 16;
        double width = Math.Max(10, Bounds.Width - pad * 2);
        double height = Math.Max(10, Bounds.Height - pad * 2);

        _item.Curves = _curves;
        _item.XMin = _xMin;
        _item.XMax = _xMax;
        _item.YMin = _yMin;
        _item.YMax = _yMax;
        _item.Color = _color;
        _item.Width = _strokeWidth;
        _item.Left = pad;
        _item.Top = pad;
        _item.PlotWidth = width;
        _item.PlotHeight = height;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            _item.Draw(context);
        }
    }
}
