namespace WallpaperMatrix.Models;

public sealed class CurvePoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public CurvePoint()
    {
    }

    public CurvePoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public CurvePoint Copy() => new(X, Y);
}

public static class FlowCurveProfiles
{
    public const string TerminalKind = "Terminal";
    public const string LengthKind = "Length";
    public const string SpeedKind = "Speed";
    public const string SignalKind = "Signal";
    public const string FilterKind = "Filter";
    public const string MemoryKind = "Memory";
    public const string HeadPulseKind = "HeadPulse";

    public static bool IsSupported(string kind) => kind is LengthKind
        or SpeedKind
        or SignalKind
        or FilterKind
        or MemoryKind
        or HeadPulseKind;

    public static bool IsIncreasing(string kind) => kind is LengthKind
        or SpeedKind
        or SignalKind
        or FilterKind
        or MemoryKind;

    public static List<CurvePoint> DefaultFor(string kind) => kind switch
    {
        LengthKind => DefaultLength(),
        SpeedKind => DefaultSpeed(),
        SignalKind => DefaultSignal(),
        FilterKind => DefaultFilter(),
        MemoryKind => DefaultMemory(),
        HeadPulseKind => DefaultHeadPulse(),
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Неизвестный тип кривой потока.")
    };

    public static List<CurvePoint> DefaultLength() => Sample(x => x);

    public static List<CurvePoint> DefaultSpeed() => Sample(x => x);

    public static List<CurvePoint> DefaultSignal() => Sample(x => x);

    public static List<CurvePoint> DefaultFilter() => Sample(x => x);

    public static List<CurvePoint> DefaultSoftFilter() => Sample(SoftFilter);

    public static List<CurvePoint> LegacyOperatorSoftFilter() =>
    [
        new(0, 0),
        new(0.125, 0.4068764466369047),
        new(0.25, 0.6416494416367646),
        new(0.375, 0.8114333262768968),
        new(0.5, 0.9132346506282233),
        new(0.625, 0.9441942529643599),
        new(0.7467700258397932, 0.9582380952380953),
        new(0.8785529715762274, 0.9677619047619048),
        new(1, 1)
    ];

    public static List<CurvePoint> DefaultMemory() => Sample(x => x);

    public static List<CurvePoint> DefaultHeadPulse() => Sample(x => 1.0 - x);

    // Retained only to recognize and migrate the former "clustered toward the
    // middle" profile to the new genuinely medium-heavy distribution.
    public static List<CurvePoint> LegacyCenteredLength() =>
        Sample(x => 0.5 + Math.Tanh((x - 0.5) * 1.65) / 1.36);

    public static IReadOnlyList<(string Id, string Name)> Presets(string kind) => kind switch
    {
        LengthKind =>
        [
            ("Uniform", "Равномерный"),
            ("Short", "Малый"),
            ("Long", "Большой"),
            ("Centered", "Средний")
        ],
        SpeedKind =>
        [
            ("Uniform", "Равномерная"),
            ("Centered", "Стабильная"),
            ("Slow", "Медленная"),
            ("Fast", "Быстрая"),
            ("Extremes", "Нестабильная")
        ],
        SignalKind =>
        [
            ("Uniform", "Равномерная"),
            ("Centered", "В основном средняя"),
            ("Short", "В основном слабая"),
            ("Long", "В основном сильная"),
            ("Extremes", "Контрастная")
        ],
        FilterKind =>
        [
            ("Uniform", "Равномерная"),
            ("Centered", "Стабильная"),
            ("Short", "Жёсткая"),
            ("Long", "Мягкая"),
            ("Extremes", "Контрастная")
        ],
        MemoryKind =>
        [
            ("Uniform", "Равномерная"),
            ("Centered", "В основном средняя"),
            ("Short", "В основном короткая"),
            ("Long", "В основном долгая"),
            ("Extremes", "Контрастная")
        ],
        HeadPulseKind =>
        [
            ("Linear", "Линейный"),
            ("Soft", "Мягкая волна"),
            ("Tight", "Короткий"),
            ("Filmic", "Кинематографический")
        ],
        _ => []
    };

    public static List<CurvePoint> Create(string kind, string preset) => kind switch
    {
        LengthKind => preset switch
        {
            "Short" => Sample(x => Math.Pow(x, 1.8)),
            "Long" => Sample(x => 1.0 - Math.Pow(1.0 - x, 1.8)),
            "Centered" => Sample(x => SymmetricPower(x, 2.3)),
            _ => DefaultLength()
        },
        SpeedKind => preset switch
        {
            "Centered" => Sample(x => SymmetricPower(x, 2.3)),
            "Slow" => Sample(x => Math.Pow(x, 1.8)),
            "Fast" => Sample(x => 1.0 - Math.Pow(1.0 - x, 1.8)),
            "Extremes" => Sample(x => SymmetricPower(x, 0.45)),
            _ => DefaultSpeed()
        },
        SignalKind => preset switch
        {
            "Centered" => Sample(x => SymmetricPower(x, 2.3)),
            "Short" => Sample(x => Math.Pow(x, 1.8)),
            "Long" => Sample(x => 1.0 - Math.Pow(1.0 - x, 1.8)),
            "Extremes" => Sample(x => SymmetricPower(x, 0.45)),
            _ => DefaultSignal()
        },
        FilterKind => preset switch
        {
            "Centered" => Sample(x => SymmetricPower(x, 2.3)),
            "Short" => Sample(x => Math.Pow(x, 1.8)),
            "Long" => DefaultSoftFilter(),
            "Extremes" => Sample(x => SymmetricPower(x, 0.45)),
            _ => DefaultFilter()
        },
        MemoryKind => preset switch
        {
            "Centered" => Sample(x => SymmetricPower(x, 2.3)),
            "Short" => Sample(x => Math.Pow(x, 1.8)),
            "Long" => Sample(x => 1.0 - Math.Pow(1.0 - x, 1.8)),
            "Extremes" => Sample(x => SymmetricPower(x, 0.45)),
            _ => DefaultMemory()
        },
        HeadPulseKind => preset switch
        {
            "Soft" => Sample(x => Math.Pow(1.0 - x, 0.58)),
            "Tight" => Sample(x => Math.Pow(1.0 - x, 3.4)),
            "Filmic" => Sample(x =>
            {
                double remaining = 1.0 - x;
                return remaining * remaining * (3.0 - 2.0 * remaining);
            }),
            _ => DefaultHeadPulse()
        },
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Неизвестный тип кривой потока.")
    };

    private static double SymmetricPower(double x, double exponent)
    {
        double centered = x * 2.0 - 1.0;
        return 0.5
            + 0.5 * Math.Sign(centered) * Math.Pow(Math.Abs(centered), exponent);
    }

    private static double SoftFilter(double x) =>
        0.96 * (1.0 - Math.Pow(1.0 - x, 4.0))
        + 0.04 * Math.Pow(x, 20.0);

    private static List<CurvePoint> Sample(Func<double, double> function)
    {
        List<CurvePoint> points = new(9);
        for (int index = 0; index <= 8; index++)
        {
            double x = index / 8.0;
            points.Add(new CurvePoint(x, Math.Clamp(function(x), 0.0, 1.0)));
        }
        return points;
    }
}

public static class FlowCurveMath
{
    public static List<CurvePoint> Normalize(
        IReadOnlyList<CurvePoint>? source,
        bool increasing)
    {
        double startY = increasing ? 0.0 : 1.0;
        double endY = increasing ? 1.0 : 0.0;
        List<CurvePoint> candidates = source?
            .Where(point => point is not null
                && double.IsFinite(point.X)
                && double.IsFinite(point.Y)
                && point.X > 0.001
                && point.X < 0.999)
            .OrderBy(point => point.X)
            .Take(10)
            .Select(point => new CurvePoint(
                Math.Clamp(point.X, 0.0, 1.0),
                Math.Clamp(point.Y, 0.0, 1.0)))
            .ToList()
            ?? [];

        List<CurvePoint> normalized = [new CurvePoint(0, startY)];
        foreach (CurvePoint candidate in candidates)
        {
            if (candidate.X - normalized[^1].X < 0.015)
                continue;
            normalized.Add(candidate);
        }
        normalized.Add(new CurvePoint(1, endY));

        double previous = startY;
        for (int index = 1; index < normalized.Count - 1; index++)
        {
            double value = increasing
                ? Math.Clamp(normalized[index].Y, previous, 1.0)
                : Math.Clamp(normalized[index].Y, 0.0, previous);
            normalized[index].Y = value;
            previous = value;
        }
        return normalized;
    }

    public static double[] BuildLookup(
        IReadOnlyList<CurvePoint>? source,
        bool increasing,
        int sampleCount = 256)
    {
        List<CurvePoint> points = Normalize(source, increasing);
        sampleCount = Math.Max(2, sampleCount);
        double[] slopes = BuildSlopes(points);
        double[] result = new double[sampleCount];
        int segment = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            double x = index / (double)(sampleCount - 1);
            while (segment + 2 < points.Count && x > points[segment + 1].X)
                segment++;
            result[index] = EvaluateSegment(points, slopes, segment, x);
        }
        result[0] = increasing ? 0.0 : 1.0;
        result[^1] = increasing ? 1.0 : 0.0;
        return result;
    }

    public static double[] BuildLookup(
        IReadOnlyList<CurvePoint>? source,
        bool increasing,
        CurveAdjustment? adjustment,
        int sampleCount = 256,
        bool invertVerticalShift = false)
    {
        double[] basis = BuildLookup(source, increasing, sampleCount);
        if (adjustment is null)
            return basis;

        CurveAdjustment normalized = adjustment.Copy();
        normalized.Normalize();
        if (Math.Abs(normalized.Character) < 0.0001
            && Math.Abs(normalized.HorizontalShift) < 0.0001
            && Math.Abs(normalized.VerticalShift) < 0.0001)
        {
            return basis;
        }

        double[] result = new double[basis.Length];
        const double maximumShift = 0.45;
        for (int index = 0; index < result.Length; index++)
        {
            double x = index / (double)(result.Length - 1);
            double shiftedX = Math.Clamp(
                x - normalized.HorizontalShift * maximumShift,
                0.0,
                1.0);
            double value = SampleLookup(basis, shiftedX);
            double uniform = increasing ? x : 1.0 - x;
            value = normalized.Character < 0
                ? value + (uniform - value) * -normalized.Character
                : uniform + (value - uniform) * (1.0 + normalized.Character * 2.0);
            double verticalShift = invertVerticalShift
                ? -normalized.VerticalShift
                : normalized.VerticalShift;
            value += verticalShift * maximumShift;
            result[index] = Math.Clamp(value, 0.0, 1.0);
        }

        result[0] = increasing ? 0.0 : 1.0;
        result[^1] = increasing ? 1.0 : 0.0;
        for (int index = 1; index < result.Length; index++)
        {
            result[index] = increasing
                ? Math.Max(result[index], result[index - 1])
                : Math.Min(result[index], result[index - 1]);
        }
        return result;
    }

    public static List<CurvePoint> ApplyAdjustment(
        IReadOnlyList<CurvePoint>? source,
        bool increasing,
        CurveAdjustment? adjustment,
        int pointCount = 9,
        bool invertVerticalShift = false)
    {
        pointCount = Math.Clamp(pointCount, 2, 12);
        double[] lookup = BuildLookup(
            source,
            increasing,
            adjustment,
            Math.Max(257, pointCount * 32),
            invertVerticalShift);
        List<CurvePoint> result = new(pointCount);
        for (int index = 0; index < pointCount; index++)
        {
            double x = index / (double)(pointCount - 1);
            result.Add(new CurvePoint(x, SampleLookup(lookup, x)));
        }
        return Normalize(result, increasing);
    }

    public static double Evaluate(
        IReadOnlyList<CurvePoint>? source,
        bool increasing,
        double x)
    {
        double[] lookup = BuildLookup(source, increasing, 257);
        return SampleLookup(lookup, x);
    }

    public static double SampleLookup(IReadOnlyList<double> lookup, double x)
    {
        if (lookup.Count == 0)
            return 0;
        if (lookup.Count == 1)
            return lookup[0];
        double position = Math.Clamp(x, 0.0, 1.0) * (lookup.Count - 1);
        int low = Math.Min((int)position, lookup.Count - 2);
        double fraction = position - low;
        return lookup[low] + (lookup[low + 1] - lookup[low]) * fraction;
    }

    public static bool Equivalent(
        IReadOnlyList<CurvePoint>? left,
        IReadOnlyList<CurvePoint>? right,
        bool increasing,
        double tolerance = 0.0001)
    {
        List<CurvePoint> normalizedLeft = Normalize(left, increasing);
        List<CurvePoint> normalizedRight = Normalize(right, increasing);
        if (normalizedLeft.Count != normalizedRight.Count)
            return false;
        for (int index = 0; index < normalizedLeft.Count; index++)
        {
            if (Math.Abs(normalizedLeft[index].X - normalizedRight[index].X) > tolerance
                || Math.Abs(normalizedLeft[index].Y - normalizedRight[index].Y) > tolerance)
            {
                return false;
            }
        }
        return true;
    }

    private static double[] BuildSlopes(IReadOnlyList<CurvePoint> points)
    {
        int count = points.Count;
        double[] slopes = new double[count];
        if (count == 2)
        {
            double slope = (points[1].Y - points[0].Y) / (points[1].X - points[0].X);
            slopes[0] = slope;
            slopes[1] = slope;
            return slopes;
        }

        double[] widths = new double[count - 1];
        double[] deltas = new double[count - 1];
        for (int index = 0; index < count - 1; index++)
        {
            widths[index] = points[index + 1].X - points[index].X;
            deltas[index] = (points[index + 1].Y - points[index].Y) / widths[index];
        }

        slopes[0] = EndpointSlope(widths[0], widths[1], deltas[0], deltas[1]);
        slopes[^1] = EndpointSlope(
            widths[^1],
            widths[^2],
            deltas[^1],
            deltas[^2]);
        for (int index = 1; index < count - 1; index++)
        {
            double before = deltas[index - 1];
            double after = deltas[index];
            if (before == 0 || after == 0 || Math.Sign(before) != Math.Sign(after))
            {
                slopes[index] = 0;
                continue;
            }
            double firstWeight = 2 * widths[index] + widths[index - 1];
            double secondWeight = widths[index] + 2 * widths[index - 1];
            slopes[index] = (firstWeight + secondWeight)
                / (firstWeight / before + secondWeight / after);
        }
        return slopes;
    }

    private static double EndpointSlope(
        double width,
        double adjacentWidth,
        double delta,
        double adjacentDelta)
    {
        double slope = ((2 * width + adjacentWidth) * delta - width * adjacentDelta)
            / (width + adjacentWidth);
        if (Math.Sign(slope) != Math.Sign(delta))
            return 0;
        if (Math.Sign(delta) != Math.Sign(adjacentDelta)
            && Math.Abs(slope) > Math.Abs(3 * delta))
        {
            return 3 * delta;
        }
        return slope;
    }

    private static double EvaluateSegment(
        IReadOnlyList<CurvePoint> points,
        IReadOnlyList<double> slopes,
        int segment,
        double x)
    {
        CurvePoint left = points[segment];
        CurvePoint right = points[segment + 1];
        double width = right.X - left.X;
        double t = width <= 0 ? 0 : Math.Clamp((x - left.X) / width, 0.0, 1.0);
        double t2 = t * t;
        double t3 = t2 * t;
        double value = (2 * t3 - 3 * t2 + 1) * left.Y
            + (t3 - 2 * t2 + t) * width * slopes[segment]
            + (-2 * t3 + 3 * t2) * right.Y
            + (t3 - t2) * width * slopes[segment + 1];
        return Math.Clamp(value, 0.0, 1.0);
    }
}
