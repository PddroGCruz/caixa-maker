using System.Globalization;

namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Utilitários numéricos compatíveis com Python 3 (arredondamento bancário half-to-even
/// e formatação invariante de ponto flutuante).
/// </summary>
public static class PythonMath
{
    /// <summary>
    /// Arredonda um número real para o inteiro mais próximo usando a regra
    /// bancária (round half to even), equivalente a <c>round(x)</c> do Python 3.
    /// </summary>
    public static int RoundHalfToEven(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "O valor deve ser finito.");
        }

        double rounded = Math.Round(value, MidpointRounding.ToEven);
        return checked((int)rounded);
    }

    /// <summary>
    /// Arredonda um número real com <paramref name="digits"/> casas decimais usando
    /// a regra bancária (half-to-even), equivalente a <c>round(x, digits)</c> do Python 3.
    /// </summary>
    public static double Round(double value, int digits)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "O valor deve ser finito.");
        }

        return Math.Round(value, digits, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Formata um número real em cultura invariante com representação decimal mínima
    /// preservando sufixo <c>.0</c> em valores inteiros, compatível com <c>str(float)</c> do Python 3.
    /// </summary>
    public static string FormatFloat(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "O valor deve ser finito.");
        }

        if (value == 0.0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        string text = value.ToString("G", CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal)
            && !text.Contains('E', StringComparison.Ordinal)
            && !text.Contains('e', StringComparison.Ordinal))
        {
            return text + ".0";
        }

        return text;
    }

    /// <summary>
    /// Formata um número inteiro ou real opcionalmente mantendo inteiros exatos sem <c>.0</c>
    /// quando se deseja espelhar literais inteiros do Python.
    /// </summary>
    public static string FormatInvariant(double value, string format)
    {
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
