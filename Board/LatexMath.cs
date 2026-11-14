using System.Text;

namespace SETBoard.Board;

/// <summary>
/// 把教学用的简化 LaTeX 转成求值器能读的普通表达式。
/// 支持 \frac{}{}、\sqrt{}、^{}、\pi、\times、\div、\left| \right| 与常用函数名；
/// 也接受不带任何命令的普通写法（如 sin(a*x)）。
/// </summary>
public static class LatexMath
{
    public static bool TryToPlain(string? latex, out string plain, out string? error)
    {
        plain = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(latex))
        {
            error = "请输入函数表达式";
            return false;
        }

        try
        {
            plain = new Converter(latex).Convert();
            if (string.IsNullOrWhiteSpace(plain))
            {
                error = "表达式为空";
                return false;
            }

            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class Converter
    {
        private string _s;
        private int _i;

        public Converter(string source) => _s = source.Trim();

        public string Convert()
        {
            if (_s.StartsWith("$$", StringComparison.Ordinal) && _s.EndsWith("$$", StringComparison.Ordinal) && _s.Length > 4)
            {
                _s = _s[2..^2];
            }
            else if (_s.StartsWith('$') && _s.EndsWith('$') && _s.Length > 2)
            {
                _s = _s[1..^1];
            }

            string result = ParseRow('\0');

            // 允许写成 y = ...，只取等号右边
            int equals = result.IndexOf('=');
            if (equals >= 0) result = result[(equals + 1)..];
            return result;
        }

        private void SkipSpace()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private string ParseRow(char terminator)
        {
            var builder = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (terminator != '\0' && c == terminator)
                {
                    _i++;
                    return builder.ToString();
                }

                if (char.IsWhiteSpace(c) || c == '~')
                {
                    // 保留空格：求值器会把它当成隐式乘法（a x → a*x）
                    builder.Append(' ');
                    _i++;
                    continue;
                }

                switch (c)
                {
                    case '{':
                        _i++;
                        builder.Append('(').Append(ParseRow('}')).Append(')');
                        break;

                    case '}':
                        throw new FormatException("花括号不匹配");

                    case '^':
                        _i++;
                        builder.Append("^(").Append(ParseGroup()).Append(')');
                        break;

                    case '_':
                        _i++;
                        ParseGroup(); // 下标只影响排版，求值时忽略
                        break;

                    case '\\':
                        builder.Append(ParseCommand());
                        break;

                    case '=':
                        builder.Append('=');
                        _i++;
                        break;

                    case '|':
                        _i++;
                        builder.Append("abs(").Append(ParseRow('|')).Append(')');
                        break;

                    case '×':
                        builder.Append('*');
                        _i++;
                        break;

                    case '÷':
                        builder.Append('/');
                        _i++;
                        break;

                    case '·':
                        builder.Append('*');
                        _i++;
                        break;

                    case '−':
                    case '–':
                        builder.Append('-');
                        _i++;
                        break;

                    case '&':
                        throw new FormatException("暂不支持多行公式");

                    default:
                        builder.Append(c);
                        _i++;
                        break;
                }
            }

            if (terminator != '\0') throw new FormatException("括号没有闭合");
            return builder.ToString();
        }

        private string ParseGroup()
        {
            SkipSpace();
            if (_i >= _s.Length) throw new FormatException("命令后面缺少内容");

            if (_s[_i] == '{')
            {
                _i++;
                return ParseRow('}');
            }

            if (_s[_i] == '\\') return ParseCommand();

            return _s[_i++].ToString();
        }

        private string ParseCommand()
        {
            _i++; // 跳过反斜杠
            int start = _i;
            while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
            string name = _s[start.._i];
            if (name.Length == 0) throw new FormatException("反斜杠后面缺少命令");

            switch (name)
            {
                case "frac":
                    return $"({ParseGroup()})/({ParseGroup()})";

                case "sqrt":
                    return $"sqrt({ParseGroup()})";

                case "pi":
                    return "pi";

                case "times":
                case "cdot":
                    return "*";

                case "div":
                    return "/";

                case "left":
                case "right":
                {
                    SkipSpace();
                    if (_i >= _s.Length) return string.Empty;
                    char delimiter = _s[_i++];
                    if (delimiter == '|') return name == "left" ? "abs(" : ")";
                    if (delimiter == '.') return string.Empty;
                    return delimiter.ToString();
                }

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
                {
                    SkipSpace();
                    if (_i < _s.Length && _s[_i] == '(')
                    {
                        _i++;
                        return name + "(" + ParseRow(')') + ")";
                    }

                    if (_i < _s.Length && _s[_i] == '{')
                    {
                        _i++;
                        return name + "(" + ParseRow('}') + ")";
                    }

                    return name + "(" + ParseGroup() + ")";
                }

                case "abs":
                    return "abs(" + ParseGroup() + ")";

                case "e":
                    return "e";

                default:
                    throw new FormatException($"不认识的命令 \\{name}");
            }
        }
    }
}
