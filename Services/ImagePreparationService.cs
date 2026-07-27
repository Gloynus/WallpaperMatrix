using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

/// <summary>
/// Converts a photograph into a compact, immutable tone map tailored to the
/// limited brightness and glyph-density range of the Matrix renderer.
/// All methods are called from the low-priority image worker.
/// </summary>
public sealed class ImagePreparationService
{
    private const int MaximumCachedImages = 10;
    private const int MaximumAnalysisWidth = 960;
    private const int MaximumAnalysisHeight = 720;
    private const int MaximumAnalysisPixels = 360_000;
    private static readonly float[] SrgbToLinear = BuildSrgbLookup();

    private readonly object _cacheLock = new();
    private readonly Dictionary<PreparationCacheKey, PreparedImage> _cache = [];
    private readonly LinkedList<PreparationCacheKey> _cacheOrder = [];

    public PreparedImage Prepare(
        ImageSourceFrame frame,
        AppSettings settings,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken,
        bool cacheResult = true)
    {
        PreparationCacheKey key = PreparationCacheKey.Create(
            frame,
            settings,
            targetWidth,
            targetHeight);
        if (cacheResult
            && TryGetCached(key, out PreparedImage cached))
            return cached;

        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource source = frame.Bitmap.Format == PixelFormats.Bgra32
            ? frame.Bitmap
            : new FormatConvertedBitmap(frame.Bitmap, PixelFormats.Bgra32, null, 0);
        int sourceWidth = source.PixelWidth;
        int sourceHeight = source.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidOperationException("Изображение не содержит пикселей.");

        int stride = checked(sourceWidth * 4);
        byte[] pixels = new byte[checked(stride * sourceHeight)];
        source.CopyPixels(pixels, stride, 0);
        (int analysisWidth, int analysisHeight) = AnalysisSize(
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight);
        float[] tone = AreaResampleBgra(
            pixels,
            sourceWidth,
            sourceHeight,
            stride,
            analysisWidth,
            analysisHeight,
            cancellationToken);

        byte[] preparedTone = string.Equals(
            settings.ImagePreparationMode,
            "None",
            StringComparison.Ordinal)
            ? Quantize(tone, cancellationToken)
            : ProcessToneMap(
                tone,
                analysisWidth,
                analysisHeight,
                settings,
                cancellationToken);
        PreparedImage prepared = new(
            preparedTone,
            analysisWidth,
            analysisHeight,
            frame.Path);
        if (cacheResult)
            AddToCache(key, prepared);
        return prepared;
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    private static byte[] ProcessToneMap(
        float[] source,
        int width,
        int height,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ImageStatistics statistics = Measure(source, width, height, cancellationToken);
        PreparationProfile profile = PreparationProfile.For(
            settings.ImagePreparationMode,
            settings,
            statistics);
        float[] global = NormalizeGlobal(
            source,
            statistics,
            profile.ShadowBalance,
            cancellationToken);
        float[] revealed = profile.LocalContrast <= 0.001
            ? global
            : ApplyClahe(
                global,
                width,
                height,
                profile.LocalContrast,
                cancellationToken);
        double[] integral = BuildIntegral(revealed, width, height, cancellationToken);
        float[] smallBlur = BoxBlur(integral, width, height, 1, cancellationToken);
        int largeRadius = Math.Clamp(
            (int)Math.Round(Math.Min(width, height) / 135.0),
            3,
            11);
        float[] largeBlur = BoxBlur(
            integral,
            width,
            height,
            largeRadius,
            cancellationToken);
        integral = [];
        float[] edges = Sobel(revealed, width, height, cancellationToken);
        float silhouetteThreshold = profile.Silhouette
            ? OtsuThreshold(revealed)
            : 0.5f;
        float[] composed = new float[revealed.Length];

        for (int index = 0; index < composed.Length; index++)
        {
            if ((index & 8191) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            float value;
            if (profile.Silhouette)
            {
                float widthAroundThreshold = 0.075f;
                float silhouette = SmoothStep(
                    silhouetteThreshold - widthAroundThreshold,
                    silhouetteThreshold + widthAroundThreshold,
                    revealed[index]);
                value = silhouette * 0.88f + edges[index] * (float)(profile.EdgeStrength * 0.46);
            }
            else if (profile.Contours)
            {
                float detail = revealed[index] - largeBlur[index];
                value = revealed[index] * 0.30f
                    + edges[index] * (float)(0.62 + profile.EdgeStrength * 0.58)
                    + detail * (float)(profile.DetailStrength * 0.48);
            }
            else
            {
                float fineDetail = revealed[index] - smallBlur[index];
                float formDetail = revealed[index] - largeBlur[index];
                value = revealed[index]
                    + (fineDetail * 0.72f + formDetail * 0.28f)
                        * (float)(profile.DetailStrength * 1.42)
                    + edges[index] * (1.0f - revealed[index])
                        * (float)(profile.EdgeStrength * 0.48);
            }
            composed[index] = Math.Clamp(value, 0.0f, 1.0f);
        }

        ApplyPaletteDistribution(
            composed,
            profile.PaletteAdaptation,
            cancellationToken);
        return Quantize(composed, cancellationToken);
    }

    private static float[] AreaResampleBgra(
        byte[] pixels,
        int sourceWidth,
        int sourceHeight,
        int stride,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        float[] result = new float[checked(targetWidth * targetHeight)];
        double scaleX = sourceWidth / (double)targetWidth;
        double scaleY = sourceHeight / (double)targetHeight;
        for (int y = 0; y < targetHeight; y++)
        {
            if ((y & 15) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double sourceTop = y * scaleY;
            double sourceBottom = (y + 1) * scaleY;
            int firstY = Math.Max(0, (int)Math.Floor(sourceTop));
            int lastY = Math.Min(sourceHeight - 1, (int)Math.Ceiling(sourceBottom) - 1);
            for (int x = 0; x < targetWidth; x++)
            {
                double sourceLeft = x * scaleX;
                double sourceRight = (x + 1) * scaleX;
                int firstX = Math.Max(0, (int)Math.Floor(sourceLeft));
                int lastX = Math.Min(sourceWidth - 1, (int)Math.Ceiling(sourceRight) - 1);
                double weighted = 0;
                double weightSum = 0;
                for (int sourceY = firstY; sourceY <= lastY; sourceY++)
                {
                    double verticalWeight = Math.Min(sourceBottom, sourceY + 1.0)
                        - Math.Max(sourceTop, sourceY);
                    int rowOffset = sourceY * stride;
                    for (int sourceX = firstX; sourceX <= lastX; sourceX++)
                    {
                        double horizontalWeight = Math.Min(sourceRight, sourceX + 1.0)
                            - Math.Max(sourceLeft, sourceX);
                        double weight = Math.Max(0, horizontalWeight * verticalWeight);
                        int pixel = rowOffset + sourceX * 4;
                        float alpha = pixels[pixel + 3] / 255.0f;
                        float linear = SrgbToLinear[pixels[pixel + 2]] * 0.2126f
                            + SrgbToLinear[pixels[pixel + 1]] * 0.7152f
                            + SrgbToLinear[pixels[pixel]] * 0.0722f;
                        weighted += Math.Pow(linear, 1.0 / 2.2) * alpha * weight;
                        weightSum += weight;
                    }
                }
                result[y * targetWidth + x] = weightSum > 0
                    ? (float)(weighted / weightSum)
                    : 0;
            }
        }
        return result;
    }

    private static (int Width, int Height) AnalysisSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale = Math.Min(
            1.0,
            Math.Min(
                Math.Min(
                    MaximumAnalysisWidth / (double)sourceWidth,
                    MaximumAnalysisHeight / (double)sourceHeight),
                Math.Min(
                    Math.Max(1, targetWidth) / (double)sourceWidth,
                    Math.Max(1, targetHeight) / (double)sourceHeight)));
        double scaledPixels = sourceWidth * scale * sourceHeight * scale;
        if (scaledPixels > MaximumAnalysisPixels)
            scale *= Math.Sqrt(MaximumAnalysisPixels / scaledPixels);
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static ImageStatistics Measure(
        float[] tone,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        int[] histogram = new int[256];
        double sum = 0;
        double squared = 0;
        for (int index = 0; index < tone.Length; index++)
        {
            if ((index & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double value = Math.Clamp(tone[index], 0.0f, 1.0f);
            histogram[Math.Clamp((int)Math.Round(value * 255), 0, 255)]++;
            sum += value;
            squared += value * value;
        }
        double mean = sum / Math.Max(1, tone.Length);
        double variance = Math.Max(0, squared / Math.Max(1, tone.Length) - mean * mean);

        double edgeSum = 0;
        int edgeCount = 0;
        int step = Math.Max(1, Math.Min(width, height) / 300);
        for (int y = step; y < height; y += step)
        {
            int row = y * width;
            int previousRow = (y - step) * width;
            for (int x = step; x < width; x += step)
            {
                edgeSum += Math.Abs(tone[row + x] - tone[row + x - step])
                    + Math.Abs(tone[row + x] - tone[previousRow + x]);
                edgeCount += 2;
            }
        }
        return new ImageStatistics(
            mean,
            Math.Sqrt(variance),
            edgeCount > 0 ? edgeSum / edgeCount : 0,
            Percentile(histogram, tone.Length, 0.02) / 255.0,
            Percentile(histogram, tone.Length, 0.98) / 255.0);
    }

    private static float[] NormalizeGlobal(
        float[] source,
        ImageStatistics statistics,
        double shadowBalance,
        CancellationToken cancellationToken)
    {
        double low = statistics.Low;
        double high = statistics.High;
        if (high - low < 0.08)
        {
            low = 0;
            high = 1;
        }
        double gamma = Math.Pow(2.0, (0.5 - shadowBalance) * 1.55);
        float[] result = new float[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double normalized = Math.Clamp((source[index] - low) / (high - low), 0.0, 1.0);
            result[index] = (float)Math.Pow(normalized, gamma);
        }
        return result;
    }

    private static float[] ApplyClahe(
        float[] source,
        int width,
        int height,
        double strength,
        CancellationToken cancellationToken)
    {
        int tilesX = Math.Clamp(width / 120, 2, 8);
        int tilesY = Math.Clamp(height / 100, 2, 8);
        float[] maps = new float[tilesX * tilesY * 256];

        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int top = tileY * height / tilesY;
            int bottom = (tileY + 1) * height / tilesY;
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                int left = tileX * width / tilesX;
                int right = (tileX + 1) * width / tilesX;
                int[] histogram = new int[256];
                for (int y = top; y < bottom; y++)
                {
                    int row = y * width;
                    for (int x = left; x < right; x++)
                    {
                        int bucket = Math.Clamp(
                            (int)Math.Round(source[row + x] * 255),
                            0,
                            255);
                        histogram[bucket]++;
                    }
                }

                int pixels = Math.Max(1, (right - left) * (bottom - top));
                int clipLimit = Math.Max(
                    2,
                    (int)Math.Ceiling(pixels / 256.0 * (1.0 + strength * 5.0)));
                int excess = 0;
                for (int bucket = 0; bucket < histogram.Length; bucket++)
                {
                    if (histogram[bucket] <= clipLimit)
                        continue;
                    excess += histogram[bucket] - clipLimit;
                    histogram[bucket] = clipLimit;
                }
                int even = excess / 256;
                int remainder = excess % 256;
                for (int bucket = 0; bucket < histogram.Length; bucket++)
                    histogram[bucket] += even + (bucket < remainder ? 1 : 0);

                int mapOffset = (tileY * tilesX + tileX) * 256;
                int cumulative = 0;
                int first = -1;
                for (int bucket = 0; bucket < histogram.Length; bucket++)
                {
                    cumulative += histogram[bucket];
                    if (first < 0 && cumulative > 0)
                        first = cumulative;
                    maps[mapOffset + bucket] = first < 0 || pixels <= first
                        ? bucket / 255.0f
                        : Math.Clamp((cumulative - first) / (float)(pixels - first), 0, 1);
                }
            }
        }

        float[] result = new float[source.Length];
        for (int y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            TileBlend yBlend = TileCoordinates(y, height, tilesY);
            for (int x = 0; x < width; x++)
            {
                TileBlend xBlend = TileCoordinates(x, width, tilesX);
                int bucket = Math.Clamp((int)Math.Round(source[y * width + x] * 255), 0, 255);
                float topLeft = maps[(yBlend.Low * tilesX + xBlend.Low) * 256 + bucket];
                float topRight = maps[(yBlend.Low * tilesX + xBlend.High) * 256 + bucket];
                float bottomLeft = maps[(yBlend.High * tilesX + xBlend.Low) * 256 + bucket];
                float bottomRight = maps[(yBlend.High * tilesX + xBlend.High) * 256 + bucket];
                float topValue = Lerp(topLeft, topRight, xBlend.Fraction);
                float bottomValue = Lerp(bottomLeft, bottomRight, xBlend.Fraction);
                float mapped = Lerp(topValue, bottomValue, yBlend.Fraction);
                result[y * width + x] = Lerp(
                    source[y * width + x],
                    mapped,
                    (float)strength);
            }
        }
        return result;
    }

    private static TileBlend TileCoordinates(int coordinate, int size, int tiles)
    {
        double value = (coordinate + 0.5) * tiles / size - 0.5;
        int low = (int)Math.Floor(value);
        double fraction = value - low;
        if (low < 0)
            return new TileBlend(0, 0, 0);
        if (low >= tiles - 1)
            return new TileBlend(tiles - 1, tiles - 1, 0);
        return new TileBlend(low, low + 1, (float)fraction);
    }

    private static double[] BuildIntegral(
        float[] source,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        int integralWidth = width + 1;
        double[] integral = new double[checked(integralWidth * (height + 1))];
        for (int y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double rowSum = 0;
            int sourceRow = y * width;
            int targetRow = (y + 1) * integralWidth;
            int previousRow = y * integralWidth;
            for (int x = 0; x < width; x++)
            {
                rowSum += source[sourceRow + x];
                integral[targetRow + x + 1] = integral[previousRow + x + 1] + rowSum;
            }
        }
        return integral;
    }

    private static float[] BoxBlur(
        double[] integral,
        int width,
        int height,
        int radius,
        CancellationToken cancellationToken)
    {
        int integralWidth = width + 1;
        float[] result = new float[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int top = Math.Max(0, y - radius);
            int bottom = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius);
                int right = Math.Min(width - 1, x + radius);
                double sum = integral[(bottom + 1) * integralWidth + right + 1]
                    - integral[top * integralWidth + right + 1]
                    - integral[(bottom + 1) * integralWidth + left]
                    + integral[top * integralWidth + left];
                int area = (right - left + 1) * (bottom - top + 1);
                result[y * width + x] = (float)(sum / area);
            }
        }
        return result;
    }

    private static float[] Sobel(
        float[] source,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        float[] result = new float[source.Length];
        for (int y = 1; y < height - 1; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int previous = (y - 1) * width;
            int current = y * width;
            int next = (y + 1) * width;
            for (int x = 1; x < width - 1; x++)
            {
                double gx =
                    -source[previous + x - 1] + source[previous + x + 1]
                    - 2 * source[current + x - 1] + 2 * source[current + x + 1]
                    - source[next + x - 1] + source[next + x + 1];
                double gy =
                    -source[previous + x - 1] - 2 * source[previous + x] - source[previous + x + 1]
                    + source[next + x - 1] + 2 * source[next + x] + source[next + x + 1];
                result[current + x] = (float)Math.Min(1.0, Math.Sqrt(gx * gx + gy * gy) / 4.0);
            }
        }
        return result;
    }

    private static void ApplyPaletteDistribution(
        float[] tone,
        double strength,
        CancellationToken cancellationToken)
    {
        if (strength <= 0.001 || tone.Length == 0)
            return;
        int[] histogram = new int[256];
        foreach (float value in tone)
            histogram[Math.Clamp((int)Math.Round(value * 255), 0, 255)]++;
        int cumulative = 0;
        int first = -1;
        float[] map = new float[256];
        for (int bucket = 0; bucket < histogram.Length; bucket++)
        {
            cumulative += histogram[bucket];
            if (first < 0 && cumulative > 0)
                first = cumulative;
            map[bucket] = first < 0 || tone.Length <= first
                ? bucket / 255.0f
                : Math.Clamp((cumulative - first) / (float)(tone.Length - first), 0, 1);
        }
        for (int index = 0; index < tone.Length; index++)
        {
            if ((index & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int bucket = Math.Clamp((int)Math.Round(tone[index] * 255), 0, 255);
            tone[index] = Lerp(tone[index], map[bucket], (float)strength);
        }
    }

    private static float OtsuThreshold(float[] source)
    {
        int[] histogram = new int[256];
        foreach (float value in source)
            histogram[Math.Clamp((int)Math.Round(value * 255), 0, 255)]++;
        double totalWeighted = 0;
        for (int index = 0; index < histogram.Length; index++)
            totalWeighted += index * histogram[index];
        int backgroundCount = 0;
        double backgroundWeighted = 0;
        double bestVariance = -1;
        int best = 127;
        for (int threshold = 0; threshold < histogram.Length; threshold++)
        {
            backgroundCount += histogram[threshold];
            if (backgroundCount == 0)
                continue;
            int foregroundCount = source.Length - backgroundCount;
            if (foregroundCount == 0)
                break;
            backgroundWeighted += threshold * histogram[threshold];
            double backgroundMean = backgroundWeighted / backgroundCount;
            double foregroundMean = (totalWeighted - backgroundWeighted) / foregroundCount;
            double difference = backgroundMean - foregroundMean;
            double variance = backgroundCount * (double)foregroundCount * difference * difference;
            if (variance <= bestVariance)
                continue;
            bestVariance = variance;
            best = threshold;
        }
        return best / 255.0f;
    }

    private static byte[] Quantize(
        float[] tone,
        CancellationToken cancellationToken)
    {
        byte[] result = new byte[tone.Length];
        for (int index = 0; index < tone.Length; index++)
        {
            if ((index & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            result[index] = (byte)Math.Clamp(
                (int)Math.Round(Math.Clamp(tone[index], 0.0f, 1.0f) * 255),
                0,
                255);
        }
        return result;
    }

    private bool TryGetCached(
        PreparationCacheKey key,
        out PreparedImage prepared)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out PreparedImage? value))
            {
                prepared = null!;
                return false;
            }
            prepared = value;
            LinkedListNode<PreparationCacheKey>? node = _cacheOrder.Find(key);
            if (node is not null)
            {
                _cacheOrder.Remove(node);
                _cacheOrder.AddLast(node);
            }
            return true;
        }
    }

    private void AddToCache(PreparationCacheKey key, PreparedImage prepared)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key))
                return;
            _cache[key] = prepared;
            _cacheOrder.AddLast(key);
            while (_cacheOrder.Count > MaximumCachedImages)
            {
                PreparationCacheKey oldest = _cacheOrder.First!.Value;
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldest);
            }
        }
    }

    private static int Percentile(int[] histogram, int total, double percentile)
    {
        int target = Math.Max(1, (int)Math.Ceiling(total * percentile));
        int count = 0;
        for (int index = 0; index < histogram.Length; index++)
        {
            count += histogram[index];
            if (count >= target)
                return index;
        }
        return 255;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / Math.Max(0.0001f, edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * amount;

    private static float[] BuildSrgbLookup()
    {
        float[] result = new float[256];
        for (int index = 0; index < result.Length; index++)
        {
            double value = index / 255.0;
            result[index] = (float)(value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4));
        }
        return result;
    }

    private readonly record struct TileBlend(int Low, int High, float Fraction);

    private readonly record struct ImageStatistics(
        double Mean,
        double StandardDeviation,
        double EdgeMean,
        double Low,
        double High);

    private readonly record struct PreparationProfile(
        double LocalContrast,
        double DetailStrength,
        double EdgeStrength,
        double ShadowBalance,
        double PaletteAdaptation,
        bool Contours,
        bool Silhouette)
    {
        public static PreparationProfile For(
            string mode,
            AppSettings settings,
            ImageStatistics statistics)
        {
            if (mode == "Portrait")
            {
                return new PreparationProfile(
                    0.38,
                    0.30,
                    0.16,
                    0.64,
                    0.10,
                    false,
                    false);
            }
            if (mode == "Contours")
            {
                return new PreparationProfile(
                    0.56,
                    0.72,
                    0.92,
                    0.48,
                    0.08,
                    true,
                    false);
            }
            if (mode == "Silhouette")
            {
                return new PreparationProfile(
                    0.34,
                    0.20,
                    0.54,
                    0.50,
                    0.04,
                    false,
                    true);
            }
            if (mode == "Custom")
            {
                return new PreparationProfile(
                    settings.ImageLocalContrast,
                    settings.ImageDetailStrength,
                    settings.ImageEdgeStrength,
                    settings.ImageShadowBalance,
                    settings.ImagePaletteAdaptation,
                    settings.ImageStructureMode == "Contours",
                    settings.ImageStructureMode == "Silhouette");
            }

            double lowContrastBoost = Math.Clamp(
                (0.19 - statistics.StandardDeviation) * 2.8,
                0.0,
                0.30);
            double quietDetailBoost = Math.Clamp(
                (0.075 - statistics.EdgeMean) * 2.4,
                0.0,
                0.18);
            double shadowBalance = Math.Clamp(
                0.52 + (0.43 - statistics.Mean) * 0.48,
                0.44,
                0.66);
            return new PreparationProfile(
                0.38 + lowContrastBoost,
                0.42 + quietDetailBoost,
                0.23 + quietDetailBoost * 0.42,
                shadowBalance,
                0.17,
                false,
                false);
        }
    }

    private readonly record struct PreparationCacheKey(
        string Path,
        long LastWriteTicks,
        long FileLength,
        int TargetWidth,
        int TargetHeight,
        string Mode,
        int LocalContrast,
        int DetailStrength,
        int EdgeStrength,
        int ShadowBalance,
        int PaletteAdaptation,
        string StructureMode)
    {
        public static PreparationCacheKey Create(
            ImageSourceFrame frame,
            AppSettings settings,
            int targetWidth,
            int targetHeight) => new(
                frame.Path.ToUpperInvariant(),
                frame.LastWriteTimeUtc.Ticks,
                frame.FileLength,
                Math.Max(1, targetWidth),
                Math.Max(1, targetHeight),
                settings.ImagePreparationMode,
                QuantizeSetting(settings.ImageLocalContrast),
                QuantizeSetting(settings.ImageDetailStrength),
                QuantizeSetting(settings.ImageEdgeStrength),
                QuantizeSetting(settings.ImageShadowBalance),
                QuantizeSetting(settings.ImagePaletteAdaptation),
                settings.ImageStructureMode);

        private static int QuantizeSetting(double value) =>
            (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 1000);
    }
}
