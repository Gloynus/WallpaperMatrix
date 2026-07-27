namespace WallpaperMatrix.Rendering;

internal static class MatrixGlyphSet
{
    private const string Ascii = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-*<>[]{}:;=?!#%&";

    public static readonly string Glyphs = Ascii + string.Concat(
        Enumerable.Range(0xFF66, 0xFF9D - 0xFF66 + 1)
            .Select(char.ConvertFromUtf32));

    public static readonly string[] GlyphStrings = Glyphs
        .Select(character => character.ToString())
        .ToArray();

    public static readonly ushort[] SparseImageGlyphs = BuildGlyphGroup(
        "1IJLT-+<>[]:;!",
        katakanaModulo: 3,
        katakanaRemainder: 0);

    public static readonly ushort[] MediumImageGlyphs = BuildGlyphGroup(
        "23457ACDEFGHKNOPQRSUVXYZ*{}=?",
        katakanaModulo: 2,
        katakanaRemainder: 1);

    public static readonly ushort[] DenseImageGlyphs = BuildGlyphGroup(
        "0689BMW#%&",
        katakanaModulo: 7,
        katakanaRemainder: 2);

    private static ushort[] BuildGlyphGroup(
        string asciiCharacters,
        int katakanaModulo,
        int katakanaRemainder)
    {
        List<ushort> indices = asciiCharacters
            .Select(character => Glyphs.IndexOf(character))
            .Where(index => index >= 0)
            .Distinct()
            .Select(index => (ushort)index)
            .ToList();

        int firstKatakana = Ascii.Length;
        for (int index = firstKatakana; index < Glyphs.Length; index++)
        {
            if ((index - firstKatakana) % katakanaModulo == katakanaRemainder)
                indices.Add((ushort)index);
        }
        return indices.Distinct().ToArray();
    }
}
