using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

public sealed class ScoreCalibratorTests
{
    private static List<LabelledScore> MonotonicData(int count)
        => Enumerable.Range(0, count)
            .Select(i => (double)i / count)
            .Select(score => new LabelledScore(score, score > 0.5 ? 1 : 0))
            .ToList();

    [Fact]
    public void Linear_Should_ReturnRawScore()
    {
        Assert.Equal(0.7, new LinearScoreCalibrator().Calibrate(0.7, []));
    }

    [Fact]
    public void Isotonic_Should_UseIsotonic_When_EnoughLabels()
    {
        Assert.False(IsotonicScoreCalibrator.Fit(MonotonicData(300)).UsesPlatt);
    }

    [Fact]
    public void Isotonic_Should_FallBackToPlatt_When_FewLabels()
    {
        Assert.True(IsotonicScoreCalibrator.Fit(MonotonicData(50)).UsesPlatt);
    }

    [Fact]
    public void Isotonic_Should_BeMonotonic_And_InRange()
    {
        IsotonicScoreCalibrator calibrator = IsotonicScoreCalibrator.Fit(MonotonicData(300));

        double low = calibrator.Calibrate(0.1, []);
        double high = calibrator.Calibrate(0.9, []);

        Assert.True(high > low);
        Assert.InRange(low, 0.0, 1.0);
        Assert.InRange(high, 0.0, 1.0);
    }

    [Fact]
    public void Platt_Should_BeMonotonic_And_InRange()
    {
        IsotonicScoreCalibrator calibrator = IsotonicScoreCalibrator.Fit(MonotonicData(50));

        double low = calibrator.Calibrate(0.1, []);
        double high = calibrator.Calibrate(0.9, []);

        Assert.True(high > low);
        Assert.InRange(low, 0.0, 1.0);
        Assert.InRange(high, 0.0, 1.0);
    }

    [Fact]
    public void Ranker_Should_FallBackToRawScore_When_NoModelConfigured()
    {
        using var calibrator = new RankerScoreCalibrator(Options.Create(new ImpactMappingOptions()), new NoopAppLogger());

        Assert.Equal(0.7, calibrator.Calibrate(0.7, new float[FeatureVectorExtractor.FeatureCount]));
    }

    [Fact]
    public void CalibrationQuality_Should_ReturnZeroBrier_ForPerfectPredictions()
    {
        CalibrationReport report = CalibrationQuality.Evaluate([(1.0, 1), (0.0, 0), (1.0, 1)]);

        Assert.Equal(0.0, report.BrierScore, 10);
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
