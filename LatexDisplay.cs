using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SETBoard;

/// <summary>
/// 轻量 LaTeX 排版控件：支持 \frac、\sqrt、上下标、常用运算符与函数名，
/// 用于把函数表达式按数学排版显示出来（分数线、根号、指数）。
/// 只做教学需要的子集，不追求完整 LaTeX。
/// </summary>
internal sealed class LatexDisplay : Control
{
    private static readonly FontFamily MathFamily = new("Cambria Math, Times New Roman");
    private static readonly IBrush FormulaBrush = Brushes.White;

    private string _latex = string.Empty;
    private double _fontSize = 26;
    private LayoutBox? _layout;
    private bool _failed;

    /// <summary>要排版的 LaTeX 文本。</summary>
    public string Latex
    {
        get => _latex;
        set
        {
            value ??= string.Empty;
            if (_latex == value) return;
            _latex = value;
            _layout = null;
            _failed = false;
            InvalidateVisual();
        }
    }

    public double FormulaFontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 0.01) return;
            _fontSize = value;
            _layout = null;
            _failed = false;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var layout = EnsureLayout();
        if (layout is null || layout.Width <= 0.1) return;

        double x = Math.Max(4, (Bounds.Width - layout.Width) / 2);
        double y = (Bounds.Height - layout.Height) / 2 + layout.Ascent;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            layout.Draw(context, new Point(x, y));
        }
    }

    private LayoutBox? EnsureLayout()
    {
        if (_layout is not null) return _layout;
        if (_failed || string.IsNullOrWhiteSpace(_latex)) return null;

        try
        {
            _layout = new LatexParser(_latex, _fontSize).Parse();
        }
        catch (FormatException)
        {
            _failed = true;
        }

        return _layout;
    }

    // ---------------- 排版盒模型 ----------------

    private abstract class LayoutBox
    {
        public double Width { get; protected set; }
        public double Ascent { get; protected set; }
        public double Descent { get; protected set; }
        public double Height => Ascent + Descent;

        /// <summary>以基线起点为原点绘制。</summary>
        public abstract void Draw(DrawingContext context, Point baseline);
    }

    private sealed class RowBox : LayoutBox
    {
        private readonly List<LayoutBox> _children;

        public RowBox(List<LayoutBox> children)
        {
            _children = children;
            foreach (var child in children)
            {
                Width += child.Width;
                Ascent = Math.Max(Ascent, child.Ascent);
                Descent = Math.Max(Descent, child.Descent);
            }
        }

        public override void Draw(DrawingContext context, Point baseline)
        {
            double x = baseline.X;
            foreach (var child in _children)
            {
                child.Draw(context, new Point(x, baseline.Y));
                x += child.Width;
            }
        }
    }

    private sealed class TextRun : LayoutBox
    {
        private readonly FormattedText _text;

        public TextRun(string text, double fontSize, bool italic)
        {
            var typeface = new Typeface(MathFamily, italic ? FontStyle.Italic : FontStyle.Normal);
            _text = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, FormulaBrush);
            Width = _text.WidthIncludingTrailingWhitespace;
            Ascent = _text.Baseline;
            Descent = Math.Max(0, _text.Height - _text.Baseline);
        }

        public override void Draw(DrawingContext context, Point baseline) =>
            context.DrawText(_text, new Point(baseline.X, baseline.Y - _text.Baseline));
    }

    private sealed class SpacerBox : LayoutBox
    {
        public SpacerBox(double width) => Width = width;
        public override void Draw(DrawingContext context, Point baseline) { }
    }

    private sealed class FractionBox : LayoutBox
    {
        private readonly LayoutBox _numerator;
        private readonly LayoutBox _denominator;
        private readonly double _gap;
        private readonly double _thickness;

        public FractionBox(LayoutBox numerator, LayoutBox denominator, double fontSize)
        {
            _numerator = numerator;
            _denominator = denominator;
            _gap = fontSize * 0.16;
            _thickness = Math.Max(1, fontSize * 0.045);
            Width = Math.Max(numerator.Width, denominator.Width) + fontSize * 0.3;
            Ascent = numerator.Height + _gap + _thickness / 2;
            Descent = denominator.Height + _gap + _thickness / 2;
        }

        public override void Draw(DrawingContext context, Point baseline)
        {
            double center = baseline.X + Width / 2;
            double barY = baseline.Y;

            double numeratorBaseline = barY - _gap - _thickness / 2 - _numerator.Descent;
            _numerator.Draw(context, new Point(center - _numerator.Width / 2, numeratorBaseline));

            double denominatorBaseline = barY + _gap + _thickness / 2 + _denominator.Ascent;
            _denominator.Draw(context, new Point(center - _denominator.Width / 2, denominatorBaseline));

            var pen = new Pen(FormulaBrush, _thickness);
            context.DrawLine(pen, new Point(baseline.X, barY), new Point(baseline.X + Width, barY));
        }
    }

    private sealed class RadicalBox : LayoutBox
    {
        private readonly LayoutBox _body;
        private readonly double _fontSize;

        public RadicalBox(LayoutBox body, double fontSize)
        {
            _body = body;
            _fontSize = fontSize;
            Width = body.Width + fontSize * 0.62;
            Ascent = body.Ascent + fontSize * 0.14;
            Descent = Math.Max(body.Descent, fontSize * 0.08);
        }

        public override void Draw(DrawingContext context, Point baseline)
        {
            double x = baseline.X;
            double height = Height;
            double top = baseline.Y - Ascent;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x, top + height * 0.72), false);
                ctx.LineTo(new Point(x + _fontSize * 0.16, top + height * 0.8));
                ctx.LineTo(new Point(x + _fontSize * 0.3, top + height));
                ctx.LineTo(new Point(x + _fontSize * 0.52, top));
                ctx.LineTo(new Point(x + Width, top));
                ctx.EndFigure(false);
            }

            var pen = new Pen(FormulaBrush, Math.Max(1, _fontSize * 0.055))
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            context.DrawGeometry(null, pen, geometry);

            _body.Draw(context, new Point(x + _fontSize * 0.62, baseline.Y));
        }
    }

    private sealed class ScriptBox : LayoutBox
    {
        private readonly LayoutBox _base;
        private readonly LayoutBox? _superscript;
        private readonly LayoutBox? _subscript;
        private readonly double _fontSize;

        public ScriptBox(LayoutBox baseBox, LayoutBox? superscript, LayoutBox? subscript, double fontSize)
        {
            _base = baseBox;
            _superscript = superscript;
            _subscript = subscript;
            _fontSize = fontSize;

            double scriptWidth = Math.Max(superscript?.Width ?? 0, subscript?.Width ?? 0);
            Width = baseBox.Width + scriptWidth * 0.86;
            Ascent = Math.Max(baseBox.Ascent, fontSize * 0.42 + (superscript?.Ascent ?? 0));
            Descent = Math.Max(baseBox.Descent, fontSize * 0.2 + (subscript?.Descent ?? 0));
        }

        public override void Draw(DrawingContext context, Point baseline)
        {
            _base.Draw(context, baseline);
            double scriptX = baseline.X + _base.Width;
            _superscript?.Draw(context, new Point(scriptX, baseline.Y - _fontSize * 0.42));
            _subscript?.Draw(context, new Point(scriptX, baseline.Y + _fontSize * 0.2));
        }
    }

    // ---------------- LaTeX 解析 ----------------

    private sealed class LatexParser
    {
        private readonly string _source;
        private int _index;
        private double _fontSize;

        public LatexParser(string source, double fontSize)
        {
            _source = source.Trim();
            if (_source.StartsWith("$$", StringComparison.Ordinal) && _source.EndsWith("$$", StringComparison.Ordinal) && _source.Length > 4)
            {
                _source = _source[2..^2];
            }
            else if (_source.StartsWith('$') && _source.EndsWith('$') && _source.Length > 2)
            {
                _source = _source[1..^1];
            }

            _fontSize = fontSize;
        }

        public LayoutBox Parse() => ParseRow('\0');

        private void SkipSpace()
        {
            while (_index < _source.Length && char.IsWhiteSpace(_source[_index])) _index++;
        }

        private LayoutBox ParseRow(char terminator)
        {
            var items = new List<LayoutBox>();
            while (_index < _source.Length)
            {
                char c = _source[_index];
                if (terminator != '\0' && c == terminator)
                {
                    _index++;
                    return new RowBox(items);
                }

                if (char.IsWhiteSpace(c) || c == '~')
                {
                    _index++;
                    continue;
                }

                if (c is '^' or '_')
                {
                    _index++;
                    double saved = _fontSize;
                    _fontSize = _fontSize * 0.72;
                    var script = ParseGroup();
                    _fontSize = saved;

                    var baseBox = items.Count > 0 ? items[^1] : new RowBox(new List<LayoutBox>());
                    if (items.Count > 0) items.RemoveAt(items.Count - 1);
                    items.Add(c == '^'
                        ? new ScriptBox(baseBox, script, null, _fontSize)
                        : new ScriptBox(baseBox, null, script, _fontSize));
                    continue;
                }

                items.Add(ParseAtom());
            }

            if (terminator != '\0') throw new FormatException("括号没有闭合");
            return new RowBox(items);
        }

        private LayoutBox ParseAtom()
        {
            char c = _source[_index];

            if (c == '{')
            {
                _index++;
                return ParseRow('}');
            }

            if (c == '\\') return ParseCommand();

            if (char.IsLetter(c))
            {
                _index++;
                return new TextRun(c.ToString(), _fontSize, italic: true);
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _index;
                while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '.')) _index++;
                return new TextRun(_source[start.._index], _fontSize, italic: false);
            }

            _index++;
            return c switch
            {
                '-' => new TextRun("−", _fontSize, false),
                '*' => new TextRun("×", _fontSize, false),
                '/' => new TextRun("/", _fontSize, false),
                '=' => new TextRun("=", _fontSize, false),
                '|' => new TextRun("|", _fontSize, false),
                _ => new TextRun(c.ToString(), _fontSize, false),
            };
        }

        private LayoutBox ParseGroup()
        {
            SkipSpace();
            if (_index >= _source.Length) throw new FormatException("命令后面缺少内容");

            if (_source[_index] == '{')
            {
                _index++;
                return ParseRow('}');
            }

            if (_source[_index] == '\\') return ParseCommand();
            return ParseAtom();
        }

        private LayoutBox ParseCommand()
        {
            _index++; // 反斜杠
            int start = _index;
            while (_index < _source.Length && char.IsLetter(_source[_index])) _index++;
            string name = _source[start.._index];
            if (name.Length == 0) throw new FormatException("反斜杠后面缺少命令");

            switch (name)
            {
                case "frac":
                {
                    var numerator = ParseGroup();
                    var denominator = ParseGroup();
                    return new FractionBox(numerator, denominator, _fontSize);
                }

                case "sqrt":
                    return new RadicalBox(ParseGroup(), _fontSize);

                case "pi":
                    return new TextRun("π", _fontSize, italic: true);

                case "times":
                    return new TextRun("×", _fontSize, false);

                case "div":
                    return new TextRun("÷", _fontSize, false);

                case "cdot":
                    return new TextRun("·", _fontSize, false);

                case "left":
                case "right":
                    SkipSpace();
                    if (_index >= _source.Length) return new SpacerBox(0);
                    if (_source[_index] == '.')
                    {
                        _index++;
                        return new SpacerBox(0);
                    }

                    return ParseAtom();

                case "sin":
                case "cos":
                case "tan":
                case "asin":
                case "acos":
                case "atan":
                case "ln":
                case "lg":
                case "log":
                case "exp":
                    return new TextRun(name, _fontSize, italic: false);

                case "abs":
                    return new RowBox(new List<LayoutBox>
                    {
                        new TextRun("|", _fontSize, false),
                        ParseGroup(),
                        new TextRun("|", _fontSize, false),
                    });

                default:
                    throw new FormatException($"不认识的命令 \\{name}");
            }
        }
    }
}
