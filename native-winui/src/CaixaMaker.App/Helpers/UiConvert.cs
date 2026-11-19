using System.Globalization;
using CaixaMaker.Core.Personalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CaixaMaker.App.Helpers;

/// <summary>Item tipado para <see cref="ComboBox"/> sem falha de reflexão em tipos aninhados privados.</summary>
public sealed class ComboOption<T>(T value, string label)
{
    public T Value { get; } = value;
    public string Label { get; } = label;

    public override string ToString() => Label;
}

/// <summary>Conversores e formatadores estáticos leves para uso com <c>x:Bind</c>.</summary>
public static class UiConvert
{
    public static Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseBoolToVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Brush HexToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return new SolidColorBrush(Colors.Gray);
        }

        string clean = hex.Trim().TrimStart('#');
        try
        {
            if (clean.Length == 6)
            {
                byte r = Convert.ToByte(clean[..2], 16);
                byte g = Convert.ToByte(clean[2..4], 16);
                byte b = Convert.ToByte(clean[4..6], 16);
                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }
            if (clean.Length == 8)
            {
                byte a = Convert.ToByte(clean[..2], 16);
                byte r = Convert.ToByte(clean[2..4], 16);
                byte g = Convert.ToByte(clean[4..6], 16);
                byte b = Convert.ToByte(clean[6..8], 16);
                return new SolidColorBrush(Color.FromArgb(a, r, g, b));
            }
        }
        catch
        {
            // Fallback abaixo
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public static bool Not(bool value) => !value;

    public static string FormatMm(double value)
        => string.Format(CultureInfo.InvariantCulture, "{0:F1} mm", value);

    public static string FormatDeg(double value)
        => string.Format(CultureInfo.InvariantCulture, "{0:0}°", Math.Round(value));

    public static string FormatQty(int qty)
        => qty == 1 ? "1 chapa" : $"{qty} chapas";

    public static string SurfaceShortName(SurfaceId id) => id switch
    {
        SurfaceId.Front => "Frente",
        SurfaceId.Back => "Trás",
        SurfaceId.Left => "Esquerda",
        SurfaceId.Right => "Direita",
        SurfaceId.Lid => "Tampa",
        SurfaceId.Bottom => "Fundo",
        _ => id.ToString(),
    };

    public static string AnchorDisplayName(AnchorMode anchor) => anchor switch
    {
        AnchorMode.Absolute => "Absoluto (mm)",
        AnchorMode.Relative => "Relativo (%)",
        AnchorMode.Center => "Centro da Face",
        AnchorMode.TopLeft => "Superior Esquerdo",
        AnchorMode.TopRight => "Superior Direito",
        AnchorMode.BottomLeft => "Inferior Esquerdo",
        AnchorMode.BottomRight => "Inferior Direito",
        _ => anchor.ToString(),
    };

    public static string OperationDisplayName(CncOperationType op) => op switch
    {
        CncOperationType.CutExternal => "Corte Externo",
        CncOperationType.CutInternal => "Corte Interno",
        CncOperationType.EngraveVector => "Gravação Vetorial",
        CncOperationType.EngraveFill => "Gravação Preenchida",
        CncOperationType.Mark => "Marcação / Risco",
        CncOperationType.EngraveRaster => "Gravação Raster",
        _ => op.ToString(),
    };

    public static string RenderModeDisplayName(TextRenderMode mode) => mode switch
    {
        TextRenderMode.Outline => "Contorno (Outline)",
        TextRenderMode.Fill => "Preenchimento (Fill)",
        TextRenderMode.Both => "Contorno + Preench.",
        TextRenderMode.Hatch => "Hachura CNC (Hatch)",
        _ => mode.ToString(),
    };

    public static string HorizontalAlignDisplayName(HorizontalAlign align) => align switch
    {
        HorizontalAlign.Left => "Esquerda",
        HorizontalAlign.Center => "Centro",
        HorizontalAlign.Right => "Direita",
        _ => align.ToString(),
    };

    public static Services.FocusControlKind ClassifyFocusedControl(object? focusedObject, out bool isReadOnly)
    {
        isReadOnly = false;
        switch (focusedObject)
        {
            case TextBox tb:
                isReadOnly = tb.IsReadOnly;
                // NumberBox interno possui TextBox filho; ainda é controle editável quando não ReadOnly
                return HasAncestorOfType<NumberBox>(tb)
                    ? Services.FocusControlKind.NumberBox
                    : Services.FocusControlKind.TextBox;
            case NumberBox:
                return Services.FocusControlKind.NumberBox;
            case RichEditBox reb:
                isReadOnly = reb.IsReadOnly;
                return Services.FocusControlKind.RichEditBox;
            case PasswordBox:
                return Services.FocusControlKind.PasswordBox;
            case AutoSuggestBox:
                return Services.FocusControlKind.AutoSuggestBox;
            case ComboBox cb when cb.IsEditable:
                return Services.FocusControlKind.EditableComboBox;
            default:
                return Services.FocusControlKind.NonEditable;
        }
    }

    private static bool HasAncestorOfType<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
}
