using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CaseLedger.Api.Services;
using Microsoft.Extensions.Logging;

namespace CaseLedger.Api.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void BusinessTelemetryUsesStructuredLevelsAndBoundedMetricLabels()
    {
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CaseLedgerTelemetry.InstrumentationName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                tags.ToArray())));
        listener.Start();

        var logger = new CapturingLogger<CaseLedgerTelemetry>();
        using var telemetry = new CaseLedgerTelemetry(logger);
        var caseId = Guid.NewGuid();

        telemetry.RecordCaseCreated(caseId, "Critical");
        telemetry.RecordEvidenceRegistered(
            caseId,
            Guid.NewGuid(),
            "application/vnd.private-case+json",
            4_096);
        telemetry.RecordAuditEventAppended(caseId, 3, "EvidenceAdded");
        telemetry.RecordAuditVerification(caseId, true, 3, null, TimeSpan.FromMilliseconds(4));
        telemetry.RecordAuditVerification(caseId, false, 2, 2, TimeSpan.FromMilliseconds(5));

        var validLog = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("events valid", StringComparison.Ordinal));
        Assert.Equal(caseId, validLog.Properties["CaseId"]);
        Assert.Equal(3, validLog.Properties["CheckedEvents"]);

        var invalidLog = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning);
        Assert.Equal(caseId, invalidLog.Properties["CaseId"]);
        Assert.Equal(2, invalidLog.Properties["CheckedEvents"]);
        Assert.Equal(2, invalidLog.Properties["BrokenAt"]);

        var evidenceLog = Assert.Single(
            logger.Entries,
            entry => entry.Message.StartsWith("Evidence ", StringComparison.Ordinal));
        Assert.Equal("application", evidenceLog.Properties["MediaTypeGroup"]);
        Assert.DoesNotContain("vnd.private-case", evidenceLog.Message, StringComparison.Ordinal);

        var evidenceMetrics = measurements
            .Where(measurement => measurement.Name == "caseledger.evidence.registered")
            .ToArray();
        Assert.NotEmpty(evidenceMetrics);
        Assert.All(
            evidenceMetrics,
            measurement => Assert.Collection(
                measurement.Tags,
                tag => Assert.Equal("caseledger.evidence.media_type_group", tag.Key)));
        Assert.Contains(
            evidenceMetrics,
            measurement => Equals(measurement.Tags[0].Value, "application"));

        Assert.All(
            measurements.Where(
                measurement => measurement.Name == "caseledger.audit.verification.duration"),
            measurement => Assert.Collection(
                measurement.Tags,
                tag => Assert.Equal("caseledger.audit.result", tag.Key)));
    }

    private sealed record MetricMeasurement(
        string Name,
        KeyValuePair<string, object?>[] Tags);

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            _ = exception;
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties));
        }
    }
}
