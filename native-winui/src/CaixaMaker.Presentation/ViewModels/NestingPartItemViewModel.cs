using System.Globalization;
using CaixaMaker.Core.Nesting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>Item observável representando uma peça na fila do Otimizador de Corte 2D (Nesting).</summary>
public sealed partial class NestingPartItemViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Action<NestingPartItemViewModel>? _onRemoved;
    private readonly Action? _onChanged;

    public NestingPart Part { get; }

    public string Id => Part.Id;
    public string Name => Part.Name;
    public string SourceTag => Part.SourceTag;
    public bool IsFromActiveBox => string.Equals(Part.SourceTag, "Caixa", StringComparison.OrdinalIgnoreCase);

    public string DimensionsLabel => string.Format(PtBr, "{0:0.0} × {1:0.0} mm", Part.WidthMm, Part.HeightMm);

    [ObservableProperty]
    private int _quantity;

    [ObservableProperty]
    private NestingRotationPolicy _rotationPolicy;

    [ObservableProperty]
    private bool _isEnabled;

    public int RotationPolicyIndex
    {
        get => RotationPolicy switch
        {
            NestingRotationPolicy.Locked0 => 0,
            NestingRotationPolicy.Step180 => 1,
            NestingRotationPolicy.Step90 => 2,
            NestingRotationPolicy.Step45 => 3,
            _ => 2,
        };
        set
        {
            NestingRotationPolicy mapped = value switch
            {
                0 => NestingRotationPolicy.Locked0,
                1 => NestingRotationPolicy.Step180,
                3 => NestingRotationPolicy.Step45,
                _ => NestingRotationPolicy.Step90,
            };
            RotationPolicy = mapped;
        }
    }

    public NestingPartItemViewModel(
        NestingPart part,
        Action<NestingPartItemViewModel>? onRemoved = null,
        Action? onChanged = null)
    {
        Part = part ?? throw new ArgumentNullException(nameof(part));
        _onRemoved = onRemoved;
        _onChanged = onChanged;

        _quantity = Math.Max(1, part.Quantity);
        _rotationPolicy = part.RotationPolicy;
        _isEnabled = part.Enabled;
    }

    partial void OnQuantityChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 500);
        if (clamped != value)
        {
            Quantity = clamped;
            return;
        }

        Part.Quantity = clamped;
        DecrementQuantityCommand.NotifyCanExecuteChanged();
        _onChanged?.Invoke();
    }

    partial void OnRotationPolicyChanged(NestingRotationPolicy value)
    {
        Part.RotationPolicy = value;
        OnPropertyChanged(nameof(RotationPolicyIndex));
        _onChanged?.Invoke();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Part.Enabled = value;
        _onChanged?.Invoke();
    }

    [RelayCommand]
    public void IncrementQuantity()
        => Quantity = Math.Min(500, Quantity + 1);

    [RelayCommand(CanExecute = nameof(CanDecrementQuantity))]
    public void DecrementQuantity()
        => Quantity = Math.Max(1, Quantity - 1);

    private bool CanDecrementQuantity() => Quantity > 1;

    [RelayCommand]
    public void Remove()
        => _onRemoved?.Invoke(this);
}
