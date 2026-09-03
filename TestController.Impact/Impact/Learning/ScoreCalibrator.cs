using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Learning;

/// <summary>A raw score paired with its observed binary outcome, used to fit a calibrator (P19).</summary>
public readonly record struct LabelledScore(double Score, int Label);

/// <summary>Maps a raw pipeline score onto a calibrated, reasoned-about scale (P19).</summary>
public interface IScoreCalibrator
{
    /// <summary>Calibrates a raw score. The feature vector is used only by model-based calibrators.</summary>
    double Calibrate(double rawScore, float[] features);
}

/// <summary>Identity pass-through calibrator (P19). The default; hand-tuned fusion remains selectable.</summary>
public sealed class LinearScoreCalibrator : IScoreCalibrator
{
    /// <inheritdoc />
    public double Calibrate(double rawScore, float[] features) => rawScore;
}

/// <summary>
/// Maps raw scores onto probabilities (P19). Fitted from labelled outcome pairs with isotonic regression
/// (pool-adjacent-violators) once enough labels exist, falling back to Platt scaling below the threshold
/// because logistic scaling is more stable on small samples. Calibration depends only on the raw score.
/// Fitting happens offline (P28), never in the request path.
/// </summary>
public sealed class IsotonicScoreCalibrator : IScoreCalibrator
{
    private const int DefaultIsotonicMinLabels = 200;

    private readonly double[] _thresholds; // block upper edges, ascending
    private readonly double[] _values;      // block means, non-decreasing
    private readonly double _plattA;
    private readonly double _plattB;

    private IsotonicScoreCalibrator(double[] thresholds, double[] values, double plattA, double plattB, bool usesPlatt)
    {
        _thresholds = thresholds;
        _values = values;
        _plattA = plattA;
        _plattB = plattB;
        UsesPlatt = usesPlatt;
    }

    /// <summary>True when the calibrator fell back to Platt scaling (fewer labels than the isotonic minimum).</summary>
    public bool UsesPlatt { get; }

    /// <summary>Fits a calibrator from labelled pairs, choosing isotonic or Platt by sample size.</summary>
    public static IsotonicScoreCalibrator Fit(IReadOnlyList<LabelledScore> data, int isotonicMinLabels = DefaultIsotonicMinLabels)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Count >= isotonicMinLabels)
        {
            (double[] thresholds, double[] values) = FitIsotonic(data);
            return new IsotonicScoreCalibrator(thresholds, values, 0, 0, usesPlatt: false);
        }

        (double a, double b) = FitPlatt(data);
        return new IsotonicScoreCalibrator([], [], a, b, usesPlatt: true);
    }

    /// <inheritdoc />
    public double Calibrate(double rawScore, float[] features)
        => UsesPlatt ? Sigmoid(_plattA * rawScore + _plattB) : Interpolate(rawScore);

    private double Interpolate(double score)
    {
        if (_thresholds.Length == 0)
        {
            return 0.5; // no fit data
        }

        int index = LowerBound(_thresholds, score);
        return index < _values.Length ? _values[index] : _values[^1];
    }

    private static (double[] thresholds, double[] values) FitIsotonic(IReadOnlyList<LabelledScore> data)
    {
        List<LabelledScore> sorted = data.OrderBy(d => d.Score).ToList();
        var sums = new List<double>();
        var counts = new List<int>();
        var edges = new List<double>();

        foreach (LabelledScore point in sorted)
        {
            sums.Add(point.Label);
            counts.Add(1);
            edges.Add(point.Score);

            // Pool adjacent violators: merge while the previous block's mean exceeds the current block's.
            while (sums.Count >= 2 && sums[^2] / counts[^2] > sums[^1] / counts[^1])
            {
                sums[^2] += sums[^1];
                counts[^2] += counts[^1];
                edges[^2] = edges[^1]; // the merged block's upper edge is the later (higher) score
                sums.RemoveAt(sums.Count - 1);
                counts.RemoveAt(counts.Count - 1);
                edges.RemoveAt(edges.Count - 1);
            }
        }

        var values = new double[sums.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = sums[i] / counts[i];
        }

        return (edges.ToArray(), values);
    }

    private static (double a, double b) FitPlatt(IReadOnlyList<LabelledScore> data)
    {
        if (data.Count == 0)
        {
            return (0, 0);
        }

        double a = 0, b = 0;
        const double learningRate = 0.3;
        for (int iteration = 0; iteration < 500; iteration++)
        {
            double gradA = 0, gradB = 0;
            foreach (LabelledScore point in data)
            {
                double p = Sigmoid(a * point.Score + b);
                double residual = p - point.Label;
                gradA += residual * point.Score;
                gradB += residual;
            }

            a += learningRate * gradA / data.Count;
            b += learningRate * gradB / data.Count;
        }

        return (a, b);
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(z));

    private static int LowerBound(double[] sortedAscending, double value)
    {
        int low = 0, high = sortedAscending.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (sortedAscending[mid] >= value)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}

/// <summary>
/// Model-based calibrator (P19) used when scoring mode is "Ranker". Loads a persisted ML.NET model from
/// <see cref="ImpactMappingOptions.LearningOptions.RankerModelPath"/> and scores the feature vector. Training
/// happens offline in the eval console (P28); if the path is empty, missing or the model fails to load, it
/// falls back to the raw score so the request path never breaks.
/// </summary>
public sealed class RankerScoreCalibrator : IScoreCalibrator, IDisposable
{
    private readonly PredictionEngine<RankerInput, RankerOutput>? _engine;

    /// <summary>Creates the calibrator, loading the model when a valid path is configured.</summary>
    public RankerScoreCalibrator(IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        string? path = options.Value.Learning.RankerModelPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _engine = null;
            return;
        }

        try
        {
            var ml = new MLContext();
            ITransformer model = ml.Model.Load(path, out _);
            _engine = ml.Model.CreatePredictionEngine<RankerInput, RankerOutput>(model);
        }
        catch (Exception ex)
        {
            logger.Error("ImpactCalibrate", "Failed to load ranker model; falling back to raw score.", ex);
            _engine = null;
        }
    }

    /// <inheritdoc />
    public double Calibrate(double rawScore, float[] features)
    {
        if (_engine is null)
        {
            return rawScore;
        }

        try
        {
            return _engine.Predict(new RankerInput { Features = features }).Score;
        }
        catch
        {
            return rawScore; // never let a scoring failure break the request path
        }
    }

    /// <inheritdoc />
    public void Dispose() => _engine?.Dispose();

    private sealed class RankerInput
    {
        [VectorType(FeatureVectorExtractor.FeatureCount)]
        public float[] Features { get; set; } = [];
    }

    private sealed class RankerOutput
    {
        public float Score { get; set; }
    }
}

/// <summary>A single reliability-curve bucket: predicted vs observed probability for a score band.</summary>
public sealed record ReliabilityBucket(double LowerEdge, double UpperEdge, int Count, double MeanPredicted, double MeanActual);

/// <summary>Calibration-quality report: overall Brier score plus reliability-curve buckets.</summary>
public sealed record CalibrationReport(double BrierScore, IReadOnlyList<ReliabilityBucket> Buckets);

/// <summary>
/// Measures calibration quality (P19) so a drifting model is visible rather than silently degrading. The
/// Brier score is the mean squared error of predicted probabilities; the reliability buckets compare mean
/// predicted against mean observed probability across score bands.
/// </summary>
public static class CalibrationQuality
{
    /// <summary>Computes the Brier score and reliability buckets from predicted/actual pairs.</summary>
    public static CalibrationReport Evaluate(IReadOnlyList<(double Predicted, int Actual)> pairs, int buckets = 10)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
        {
            return new CalibrationReport(0, []);
        }

        double brier = pairs.Average(p => (p.Predicted - p.Actual) * (p.Predicted - p.Actual));

        int bucketCount = Math.Max(1, buckets);
        var report = new List<ReliabilityBucket>(bucketCount);
        for (int b = 0; b < bucketCount; b++)
        {
            double lower = (double)b / bucketCount;
            double upper = (double)(b + 1) / bucketCount;
            List<(double Predicted, int Actual)> inBucket = pairs
                .Where(p => p.Predicted >= lower && (p.Predicted < upper || (b == bucketCount - 1 && p.Predicted <= upper)))
                .ToList();

            if (inBucket.Count > 0)
            {
                report.Add(new ReliabilityBucket(
                    lower, upper, inBucket.Count,
                    inBucket.Average(p => p.Predicted),
                    inBucket.Average(p => (double)p.Actual)));
            }
        }

        return new CalibrationReport(brier, report);
    }
}
