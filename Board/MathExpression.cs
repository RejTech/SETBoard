using System.Globalization;

namespace SETBoard.Board;

/// <summary>
/// 教学用的函数表达式解析与求值。支持 + - * / ^、括号、隐式乘法（如 2x）、
/// 常量 pi/e、变量 x 与参数 a，以及 sin cos tan 等常用函数。
/// 递归下降解析成表达式树，求值不抛异常（非法运算得到 NaN / Infinity）。
/// </summary>
public sealed class MathExpression
{
    private readonly Node _root;

    private MathExpression(Node root, bool usesParameter)
    {
        _root = root;
        UsesParameter = usesParameter;
    }

    /// <summary>表达式是否用到了参数 a。</summary>
    public bool UsesParameter { get; }

    public double Evaluate(double x, double a) => _root.Evaluate(x, a);

    public static bool TryParse(string? source, out MathExpression? expression, out string? error)
    {
        expression = null;
        error = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "请输入函数表达式";
            return false;
        }

        try
        {
            var parser = new Parser(Tokenize(source));
            var root = parser.Parse();
            expression = new MathExpression(root, parser.UsesParameter);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception)
        {
            error = "表达式无法解析";
            return false;
        }
    }

    // ---------------- 词法 ----------------

    private enum TokenKind
    {
        Number,
        Ident,
        Operator,
        LParen,
        RParen,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, double Number);

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                bool dot = false;
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.'))
                {
                    if (source[i] == '.')
                    {
                        if (dot) break;
                        dot = true;
                    }

                    i++;
                }

                string text = source[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new FormatException($"数字 “{text}” 无效");
                }

                tokens.Add(new Token(TokenKind.Number, text, value));
                continue;
            }

            if (char.IsLetter(c) || c == 'π')
            {
                int start = i;
                while (i < source.Length && (char.IsLetter(source[i]) || source[i] == 'π'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Ident, source[start..i].ToLowerInvariant(), 0));
                continue;
            }

            switch (c)
            {
                case '+':
                case '-':
                case '*':
                case '/':
                case '^':
                    tokens.Add(new Token(TokenKind.Operator, c.ToString(), 0));
                    i++;
                    continue;
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", 0));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", 0));
                    i++;
                    continue;
                case ',':
                    throw new FormatException("暂不支持多参数函数");
                case '×':
                case '·':
                    tokens.Add(new Token(TokenKind.Operator, "*", 0));
                    i++;
                    continue;
                case '÷':
                    tokens.Add(new Token(TokenKind.Operator, "/", 0));
                    i++;
                    continue;
                case '−':
                case '–':
                    tokens.Add(new Token(TokenKind.Operator, "-", 0));
                    i++;
                    continue;
                default:
                    throw new FormatException($"无法识别的字符 “{c}”");
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, 0));
        return tokens;
    }

    // ---------------- 语法 ----------------

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _index;

        public Parser(List<Token> tokens) => _tokens = tokens;

        public bool UsesParameter { get; private set; }

        private Token Current => _tokens[_index];

        private Token Next()
        {
            var token = _tokens[_index];
            if (token.Kind != TokenKind.End) _index++;
            return token;
        }

        private bool MatchOperator(string text)
        {
            if (Current.Kind == TokenKind.Operator && Current.Text == text)
            {
                _index++;
                return true;
            }

            return false;
        }

        private static bool StartsAtom(Token token) =>
            token.Kind is TokenKind.Number or TokenKind.Ident or TokenKind.LParen;

        public Node Parse()
        {
            var node = ParseExpression();
            if (Current.Kind != TokenKind.End)
            {
                throw new FormatException("表达式末尾有多余内容，或缺少运算符");
            }

            return node;
        }

        private Node ParseExpression()
        {
            var node = ParseTerm();
            while (true)
            {
                if (MatchOperator("+")) node = new BinaryNode('+', node, ParseTerm());
                else if (MatchOperator("-")) node = new BinaryNode('-', node, ParseTerm());
                else return node;
            }
        }

        private Node ParseTerm()
        {
            var node = ParseUnary();
            while (true)
            {
                if (MatchOperator("*")) node = new BinaryNode('*', node, ParseUnary());
                else if (MatchOperator("/")) node = new BinaryNode('/', node, ParseUnary());
                else if (StartsAtom(Current)) node = new BinaryNode('*', node, ParseUnary()); // 隐式乘法：2x、3(x+1)
                else return node;
            }
        }

        private Node ParseUnary()
        {
            if (MatchOperator("-")) return new NegateNode(ParseUnary());
            if (MatchOperator("+")) return ParseUnary();
            return ParsePower();
        }

        private Node ParsePower()
        {
            var basis = ParseAtom();
            // 幂右结合：2^3^2 = 2^(3^2)
            if (MatchOperator("^")) return new BinaryNode('^', basis, ParseUnary());
            return basis;
        }

        private Node ParseAtom()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                    _index++;
                    return new NumberNode(token.Number);

                case TokenKind.LParen:
                {
                    _index++;
                    var inner = ParseExpression();
                    if (Current.Kind != TokenKind.RParen) throw new FormatException("括号没有闭合");
                    _index++;
                    return inner;
                }

                case TokenKind.Ident:
                    _index++;
                    return ParseIdent(token.Text);

                default:
                    throw new FormatException("这里需要一个数字、x、a 或左括号");
            }
        }

        private Node ParseIdent(string name)
        {
            // 函数：名字后面必须跟括号
            if (FunctionNode.IsFunction(name))
            {
                if (Current.Kind != TokenKind.LParen)
                {
                    throw new FormatException($"函数 {name} 后面需要括号，例如 {name}(x)");
                }

                _index++;
                var argument = ParseExpression();
                if (Current.Kind != TokenKind.RParen) throw new FormatException("括号没有闭合");
                _index++;
                return new FunctionNode(name, argument);
            }

            return name switch
            {
                "x" => new VariableNode(false),
                "a" => UsesParameter ? new VariableNode(true) : SetParameter(),
                "pi" or "π" => new NumberNode(Math.PI),
                "e" => new NumberNode(Math.E),
                _ => throw new FormatException($"未知符号 “{name}”，可用 x、a、pi、e 或函数名"),
            };
        }

        private Node SetParameter()
        {
            UsesParameter = true;
            return new VariableNode(true);
        }
    }

    // ---------------- 表达式树 ----------------

    private abstract class Node
    {
        public abstract double Evaluate(double x, double a);
    }

    private sealed class NumberNode(double value) : Node
    {
        public override double Evaluate(double x, double a) => value;
    }

    private sealed class VariableNode(bool isParameter) : Node
    {
        public override double Evaluate(double x, double a) => isParameter ? a : x;
    }

    private sealed class NegateNode(Node inner) : Node
    {
        public override double Evaluate(double x, double a) => -inner.Evaluate(x, a);
    }

    private sealed class BinaryNode(char op, Node left, Node right) : Node
    {
        public override double Evaluate(double x, double a)
        {
            double l = left.Evaluate(x, a);
            double r = right.Evaluate(x, a);
            return op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                '/' => l / r,
                '^' => Math.Pow(l, r),
                _ => double.NaN,
            };
        }
    }

    private sealed class FunctionNode : Node
    {
        private readonly string _name;
        private readonly Node _argument;

        public FunctionNode(string name, Node argument)
        {
            _name = name;
            _argument = argument;
        }

        public static bool IsFunction(string name) => name is
            "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or
            "sqrt" or "abs" or "ln" or "lg" or "log" or "exp" or
            "floor" or "ceil" or "round" or "sign" or "sinh" or "cosh" or "tanh";

        public override double Evaluate(double x, double a)
        {
            double v = _argument.Evaluate(x, a);
            return _name switch
            {
                "sin" => Math.Sin(v),
                "cos" => Math.Cos(v),
                "tan" => Math.Tan(v),
                "asin" => Math.Asin(v),
                "acos" => Math.Acos(v),
                "atan" => Math.Atan(v),
                "sqrt" => Math.Sqrt(v),
                "abs" => Math.Abs(v),
                "ln" => Math.Log(v),
                "lg" or "log" => Math.Log10(v),
                "exp" => Math.Exp(v),
                "floor" => Math.Floor(v),
                "ceil" => Math.Ceiling(v),
                "round" => Math.Round(v),
                "sign" => Math.Sign(v),
                "sinh" => Math.Sinh(v),
                "cosh" => Math.Cosh(v),
                "tanh" => Math.Tanh(v),
                _ => double.NaN,
            };
        }
    }
}
