using System.Globalization;

namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Avaliador determinístico de expressões aritméticas inline para campos numéricos CAD,
/// implementado via parser descendente recursivo sem reflexão nem <c>DataTable.Compute</c>.
/// </summary>
public static class CadMathExpressionEvaluator
{
    /// <summary>
    /// Avalia uma expressão matemática simples contendo números reais (com ponto ou vírgula),
    /// operadores binários (<c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>), operadores unários (<c>+</c>, <c>-</c>)
    /// e parênteses aninhados. Retorna <see langword="false"/> em sintaxe inválida, NaN, infinito ou divisão por zero.
    /// </summary>
    public static bool TryEvaluate(string? expression, out double result)
    {
        result = 0.0;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var parser = new ExpressionParser(expression);
        if (!parser.TryParseExpression(out double value))
        {
            return false;
        }

        parser.SkipWhitespace();
        if (!parser.IsAtEnd || !double.IsFinite(value))
        {
            return false;
        }

        result = value == 0.0 ? 0.0 : value;
        return true;
    }

    private ref struct ExpressionParser
    {
        private readonly string _text;
        private int _pos;

        public ExpressionParser(string text)
        {
            _text = text;
            _pos = 0;
        }

        public readonly bool IsAtEnd => _pos >= _text.Length;

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        public bool TryParseExpression(out double value)
        {
            if (!TryParseTerm(out value))
            {
                return false;
            }

            while (true)
            {
                SkipWhitespace();
                if (IsAtEnd)
                {
                    return true;
                }

                char op = _text[_pos];
                if (op is not ('+' or '-'))
                {
                    return true;
                }

                _pos++;
                if (!TryParseTerm(out double rhs))
                {
                    return false;
                }

                value = op == '+' ? value + rhs : value - rhs;
                if (!double.IsFinite(value))
                {
                    return false;
                }
            }
        }

        private bool TryParseTerm(out double value)
        {
            if (!TryParseUnary(out value))
            {
                return false;
            }

            while (true)
            {
                SkipWhitespace();
                if (IsAtEnd)
                {
                    return true;
                }

                char op = _text[_pos];
                if (op is not ('*' or '/'))
                {
                    return true;
                }

                _pos++;
                if (!TryParseUnary(out double rhs))
                {
                    return false;
                }

                if (op == '*')
                {
                    value *= rhs;
                }
                else
                {
                    if (rhs == 0.0)
                    {
                        return false;
                    }

                    value /= rhs;
                }

                if (!double.IsFinite(value))
                {
                    return false;
                }
            }
        }

        private bool TryParseUnary(out double value)
        {
            SkipWhitespace();
            if (IsAtEnd)
            {
                value = 0.0;
                return false;
            }

            char ch = _text[_pos];
            if (ch is '+' or '-')
            {
                _pos++;
                if (!TryParseUnary(out double inner))
                {
                    value = 0.0;
                    return false;
                }

                value = ch == '-' ? -inner : inner;
                return double.IsFinite(value);
            }

            return TryParsePrimary(out value);
        }

        private bool TryParsePrimary(out double value)
        {
            SkipWhitespace();
            if (IsAtEnd)
            {
                value = 0.0;
                return false;
            }

            if (_text[_pos] == '(')
            {
                _pos++;
                if (!TryParseExpression(out value))
                {
                    return false;
                }

                SkipWhitespace();
                if (IsAtEnd || _text[_pos] != ')')
                {
                    value = 0.0;
                    return false;
                }

                _pos++;
                return true;
            }

            return TryParseNumber(out value);
        }

        private bool TryParseNumber(out double value)
        {
            value = 0.0;
            int start = _pos;
            bool hasDigit = false;
            bool hasDecimalSeparator = false;

            while (_pos < _text.Length)
            {
                char ch = _text[_pos];
                if (char.IsAsciiDigit(ch))
                {
                    hasDigit = true;
                    _pos++;
                }
                else if (ch is '.' or ',')
                {
                    if (hasDecimalSeparator)
                    {
                        return false;
                    }

                    hasDecimalSeparator = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (!hasDigit)
            {
                _pos = start;
                return false;
            }

            string slice = _text.Substring(start, _pos - start).Replace(',', '.');
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            return double.IsFinite(value);
        }
    }
}
