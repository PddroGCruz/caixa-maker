using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;

namespace CaixaMaker.Cam.Text;

/// <summary>Descritor de uma fonte TrueType/OpenType descoberta no sistema.</summary>
public sealed record FontDescriptor(
    string Family,
    string FullName,
    string Style,
    bool IsBold,
    bool IsItalic,
    string Path,
    int TtcIndex = 0);

/// <summary>
/// Descobre, indexa e resolve fontes TrueType/OpenType do Windows (incluindo fontes de usuário)
/// com tabela de fallback compatível com <c>FontResolver</c> da v1.2.0.
/// </summary>
public sealed class FontResolver
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf",
        ".otf",
        ".ttc",
    };

    private static readonly IReadOnlyDictionary<string, string[]> AliasMap =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["arial"] = ["arial", "liberation sans", "dejavu sans", "noto sans", "helvetica"],
            ["helvetica"] = ["helvetica", "arial", "liberation sans", "dejavu sans", "noto sans"],
            ["times"] = ["times new roman", "liberation serif", "dejavu serif", "noto serif"],
            ["times new roman"] = ["times new roman", "liberation serif", "dejavu serif", "noto serif"],
            ["courier"] = ["courier new", "liberation mono", "dejavu sans mono", "noto sans mono"],
            ["courier new"] = ["courier new", "liberation mono", "dejavu sans mono", "noto sans mono"],
            ["sans"] = ["arial", "segoe ui", "liberation sans", "dejavu sans", "noto sans"],
            ["sans-serif"] = ["arial", "segoe ui", "liberation sans", "dejavu sans", "noto sans"],
            ["serif"] = ["times new roman", "georgia", "liberation serif", "dejavu serif", "noto serif"],
            ["mono"] = ["consolas", "courier new", "liberation mono", "dejavu sans mono"],
            ["monospace"] = ["consolas", "courier new", "liberation mono", "dejavu sans mono"],
            ["segoe ui"] = ["segoe ui", "arial", "liberation sans", "dejavu sans"],
            ["calibri"] = ["calibri", "carlito", "liberation sans", "dejavu sans"],
            ["verdana"] = ["verdana", "dejavu sans", "liberation sans"],
        };

    private static readonly string[] GlobalFallbackOrder =
    [
        "arial",
        "segoe ui",
        "dejavu sans",
        "liberation sans",
        "noto sans",
        "times new roman",
    ];

    private const uint TagName = 0x6E616D65; // 'name'

    private readonly IReadOnlyList<string> _searchDirs;
    private readonly Lazy<Dictionary<string, List<FontDescriptor>>> _index;
    private readonly ConcurrentDictionary<(string Path, int TtcIndex), SKTypeface> _typefaceCache = new();

    public FontResolver(IEnumerable<string>? extraDirs = null)
    {
        var dirs = new List<string>();
        if (extraDirs is not null)
        {
            dirs.AddRange(extraDirs.Where(d => !string.IsNullOrWhiteSpace(d)));
        }

        dirs.AddRange(GetDefaultFontDirs());
        _searchDirs = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _index = new Lazy<Dictionary<string, List<FontDescriptor>>>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static FontResolver Default { get; } = new();

    public static IReadOnlyList<string> GetDefaultFontDirs()
    {
        var dirs = new List<string>();
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrWhiteSpace(fontsDir) && Directory.Exists(fontsDir))
        {
            dirs.Add(fontsDir);
        }

        string winDir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        string winFonts = Path.Combine(winDir, "Fonts");
        if (Directory.Exists(winFonts) && !dirs.Contains(winFonts, StringComparer.OrdinalIgnoreCase))
        {
            dirs.Add(winFonts);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string userFonts = Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
            if (Directory.Exists(userFonts) && !dirs.Contains(userFonts, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(userFonts);
            }
        }

        return dirs;
    }

    /// <summary>Lista todas as fontes indexadas no sistema.</summary>
    public IReadOnlyList<FontDescriptor> ListFonts()
    {
        Dictionary<string, List<FontDescriptor>> map = _index.Value;
        return map.Values
            .SelectMany(list => list)
            .OrderBy(d => d.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.IsBold)
            .ThenBy(d => d.IsItalic)
            .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Lista os nomes canônicos de famílias tipográficas disponíveis.</summary>
    public IReadOnlyList<string> ListFamilies()
    {
        Dictionary<string, List<FontDescriptor>> map = _index.Value;
        return map.Values
            .Where(list => list.Count > 0)
            .Select(list => list[0].Family)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolve o descritor da fonte mais adequada e indica se houve fallback para outra família.
    /// </summary>
    public (FontDescriptor Descriptor, bool FallbackUsed) Resolve(
        string fontFamily = "Arial",
        bool bold = false,
        bool italic = false,
        string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            IReadOnlyList<FontDescriptor> explicitDescs = ReadDescriptorsFromFile(explicitPath);
            if (explicitDescs.Count > 0)
            {
                return (PickStyle(explicitDescs, bold, italic), false);
            }
        }

        Dictionary<string, List<FontDescriptor>> map = _index.Value;
        if (map.Count == 0)
        {
            throw new InvalidOperationException("Nenhuma fonte TrueType/OpenType foi encontrada no sistema.");
        }

        string target = (fontFamily ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target))
        {
            target = "arial";
        }

        // 1. Correspondência exata por nome normalizado da família
        if (map.TryGetValue(target, out List<FontDescriptor>? exactMatches) && exactMatches.Count > 0)
        {
            return (PickStyle(exactMatches, bold, italic), false);
        }

        // 2. Correspondência via mapa de aliases
        if (AliasMap.TryGetValue(target, out string[]? aliasCandidates))
        {
            foreach (string cand in aliasCandidates)
            {
                if (map.TryGetValue(cand, out List<FontDescriptor>? candMatches) && candMatches.Count > 0)
                {
                    bool fallbackUsed = !string.Equals(cand, target, StringComparison.OrdinalIgnoreCase);
                    return (PickStyle(candMatches, bold, italic), fallbackUsed);
                }
            }
        }

        // 3. Correspondência parcial
        foreach ((string fam, List<FontDescriptor> matches) in map)
        {
            if (matches.Count > 0 && (fam.Contains(target, StringComparison.Ordinal) || target.Contains(fam, StringComparison.Ordinal)))
            {
                return (PickStyle(matches, bold, italic), false);
            }
        }

        // 4. Fallback global explícito
        foreach (string fb in GlobalFallbackOrder)
        {
            if (map.TryGetValue(fb, out List<FontDescriptor>? fbMatches) && fbMatches.Count > 0)
            {
                return (PickStyle(fbMatches, bold, italic), true);
            }
        }

        // 5. Fallback para a primeira fonte disponível
        foreach (string key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            List<FontDescriptor> anyMatches = map[key];
            if (anyMatches.Count > 0)
            {
                return (PickStyle(anyMatches, bold, italic), true);
            }
        }

        throw new InvalidOperationException("Não foi possível resolver nenhuma fonte disponível.");
    }

    internal SKTypeface LoadTypeface(FontDescriptor descriptor)
    {
        return _typefaceCache.GetOrAdd(
            (descriptor.Path, descriptor.TtcIndex),
            key => SKTypeface.FromFile(key.Path, key.TtcIndex)
                ?? throw new InvalidOperationException($"Falha ao carregar arquivo de fonte '{key.Path}'."));
    }

    private Dictionary<string, List<FontDescriptor>> BuildIndex()
    {
        var map = new Dictionary<string, List<FontDescriptor>>(StringComparer.Ordinal);
        foreach (string directory in _searchDirs)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(p => SupportedExtensions.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                foreach (FontDescriptor desc in ReadDescriptorsFromFile(file))
                {
                    string key = desc.Family.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (!map.TryGetValue(key, out List<FontDescriptor>? list))
                    {
                        list = new List<FontDescriptor>();
                        map[key] = list;
                    }

                    list.Add(desc);
                }
            }
        }

        return map;
    }

    private static IReadOnlyList<FontDescriptor> ReadDescriptorsFromFile(string path)
    {
        var result = new List<FontDescriptor>();
        try
        {
            int count = GetFaceCount(path);
            for (int index = 0; index < count; index++)
            {
                using SKTypeface? typeface = SKTypeface.FromFile(path, index);
                if (typeface is null)
                {
                    continue;
                }

                (string? prefFamily, string? prefStyle, string? winFamily, string? winStyle, string? fullName) =
                    ReadOpenTypeNames(typeface);

                string family = !string.IsNullOrWhiteSpace(prefFamily)
                    ? prefFamily
                    : !string.IsNullOrWhiteSpace(winFamily)
                        ? winFamily
                        : typeface.FamilyName;

                if (string.IsNullOrWhiteSpace(family))
                {
                    continue;
                }

                string style = !string.IsNullOrWhiteSpace(prefStyle)
                    ? prefStyle
                    : !string.IsNullOrWhiteSpace(winStyle)
                        ? winStyle
                        : InferStyleName(typeface);

                bool isBold = typeface.FontWeight >= (int)SKFontStyleWeight.SemiBold
                    || style.Contains("bold", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("black", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("heavy", StringComparison.OrdinalIgnoreCase);

                bool isItalic = typeface.FontSlant != SKFontStyleSlant.Upright
                    || style.Contains("italic", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("oblique", StringComparison.OrdinalIgnoreCase);

                string resolvedFullName = !string.IsNullOrWhiteSpace(fullName)
                    ? fullName
                    : $"{family} {style}".Trim();

                result.Add(new FontDescriptor(
                    Family: family.Trim(),
                    FullName: resolvedFullName.Trim(),
                    Style: style.Trim(),
                    IsBold: isBold,
                    IsItalic: isItalic,
                    Path: path,
                    TtcIndex: index));
            }
        }
        catch
        {
            // Ignora arquivos corrompidos ou incompatíveis durante a varredura de fontes do sistema.
        }

        return result;
    }

    private static int GetFaceCount(string path)
    {
        if (!path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        Span<byte> header = stackalloc byte[12];
        using FileStream fs = File.OpenRead(path);
        if (fs.Read(header) < 12)
        {
            return 1;
        }

        if (header[0] == (byte)'t' && header[1] == (byte)'t' && header[2] == (byte)'c' && header[3] == (byte)'f')
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
            return count is > 0 and <= 64 ? (int)count : 1;
        }

        return 1;
    }

    private static (string? PrefFamily, string? PrefStyle, string? WinFamily, string? WinStyle, string? FullName)
        ReadOpenTypeNames(SKTypeface typeface)
    {
        if (!typeface.TryGetTableData(TagName, out byte[]? data) || data is null || data.Length < 6)
        {
            return (null, null, null, null, null);
        }

        ReadOnlySpan<byte> span = data;
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
        ushort stringOffset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));

        string? prefFamily = null;
        string? prefStyle = null;
        string? winFamily = null;
        string? winStyle = null;
        string? fullName = null;

        for (int i = 0; i < count; i++)
        {
            int recOffset = 6 + (i * 12);
            if (recOffset + 12 > span.Length)
            {
                break;
            }

            ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset, 2));
            ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset + 2, 2));
            ushort languageId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset + 4, 2));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset + 6, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset + 8, 2));
            ushort offset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(recOffset + 10, 2));

            if (nameId is not (1 or 2 or 4 or 16 or 17))
            {
                continue;
            }

            int start = stringOffset + offset;
            if (start < 0 || start + length > span.Length || length == 0)
            {
                continue;
            }

            string decoded = DecodeNameRecord(span.Slice(start, length), platformId, encodingId);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                continue;
            }

            bool isPreferredLang = (platformId == 3 && languageId == 0x0409) || platformId == 0;
            switch (nameId)
            {
                case 1:
                    if (winFamily is null || isPreferredLang)
                    {
                        winFamily = decoded;
                    }
                    break;
                case 2:
                    if (winStyle is null || isPreferredLang)
                    {
                        winStyle = decoded;
                    }
                    break;
                case 4:
                    if (fullName is null || isPreferredLang)
                    {
                        fullName = decoded;
                    }
                    break;
                case 16:
                    if (prefFamily is null || isPreferredLang)
                    {
                        prefFamily = decoded;
                    }
                    break;
                case 17:
                    if (prefStyle is null || isPreferredLang)
                    {
                        prefStyle = decoded;
                    }
                    break;
            }
        }

        return (prefFamily, prefStyle, winFamily, winStyle, fullName);
    }

    private static string DecodeNameRecord(ReadOnlySpan<byte> bytes, ushort platformId, ushort encodingId)
    {
        if (platformId == 3 || platformId == 0 || (platformId == 2 && encodingId == 1))
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes).Trim('\0', ' ');
        }

        return System.Text.Encoding.Latin1.GetString(bytes).Trim('\0', ' ');
    }

    private static string InferStyleName(SKTypeface typeface)
    {
        bool bold = typeface.FontWeight >= (int)SKFontStyleWeight.SemiBold;
        bool italic = typeface.FontSlant != SKFontStyleSlant.Upright;
        return (bold, italic) switch
        {
            (true, true) => "Bold Italic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };
    }

    private static FontDescriptor PickStyle(IReadOnlyList<FontDescriptor> candidates, bool bold, bool italic)
    {
        return candidates
            .OrderBy(d => d.IsBold == bold && d.IsItalic == italic ? 0 : 1)
            .ThenBy(d => (d.IsBold != bold ? 1 : 0) + (d.IsItalic != italic ? 1 : 0))
            .ThenBy(d => (d.IsBold || d.IsItalic) ? 1 : 0)
            .ThenBy(d => d.Path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
