namespace CaixaMaker.Core.BoxModels;

/// <summary>
/// Registro central de modelos paramétricos disponíveis no aplicativo.
/// Porta integralmente <c>BoxModelRegistry</c> de <c>box_models/registry.py</c>.
/// </summary>
public sealed class BoxModelRegistry
{
    private readonly Dictionary<string, IBoxModelDefinition> _models = new(StringComparer.Ordinal);
    private readonly List<string> _orderedIds = new();

    private static readonly Lazy<BoxModelRegistry> LazyDefault = new(CreateDefault);

    /// <summary>Instância padrão pré-populada com os modelos oficiais.</summary>
    public static BoxModelRegistry Default => LazyDefault.Value;

    /// <summary>Cria um registro padrão populado na ordem canônica do catálogo.</summary>
    public static BoxModelRegistry CreateDefault()
    {
        var registry = new BoxModelRegistry();
        registry.Register(new BoxWithLidAndHandlesModel());
        registry.Register(new OpenBoxModel());
        return registry;
    }

    /// <summary>Registra (ou atualiza mantendo a ordem) uma definição de modelo.</summary>
    public void Register(IBoxModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.ModelId))
        {
            throw new ArgumentException("O model_id do modelo não pode estar vazio.", nameof(definition));
        }

        if (!_models.ContainsKey(definition.ModelId))
        {
            _orderedIds.Add(definition.ModelId);
        }

        _models[definition.ModelId] = definition;
    }

    /// <summary>Retorna o modelo registrado ou lança <see cref="KeyNotFoundException"/>.</summary>
    public IBoxModelDefinition Get(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || !_models.TryGetValue(modelId, out IBoxModelDefinition? model))
        {
            string available = string.Join(", ", _orderedIds);
            throw new KeyNotFoundException(
                $"Modelo de caixa desconhecido: '{modelId}'. Disponíveis: {available}");
        }

        return model;
    }

    /// <summary>Tenta recuperar o modelo registrado pelo identificador.</summary>
    public bool TryGet(string modelId, out IBoxModelDefinition? model)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            model = null;
            return false;
        }

        return _models.TryGetValue(modelId, out model);
    }

    public bool Contains(string modelId)
        => !string.IsNullOrWhiteSpace(modelId) && _models.ContainsKey(modelId);

    public IReadOnlyList<string> ListIds() => _orderedIds.AsReadOnly();

    public IReadOnlyList<IBoxModelDefinition> ListModels()
        => _orderedIds.Select(id => _models[id]).ToArray();

    public IReadOnlyList<BoxModelCatalogItem> ListCatalogItems()
        => _orderedIds.Select(id => _models[id].ToCatalogItem()).ToArray();
}
