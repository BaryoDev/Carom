using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;
using Carom.Telemetry.OpenTelemetry;

namespace Carom.Telemetry.OpenTelemetry.Tests;

/// <summary>
/// Proves every instrument the package advertises actually records when its Record method
/// is called, using MeterListener so no exporter is needed. All tests live in one class:
/// the meter and its instruments are static, and a single collection keeps the recorded
/// measurements attributable to the test that made them.
/// </summary>
[Collection("CaromMeter")]
public class CaromTelemetryTests
{
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

    /// <summary>
    /// Runs the action with a listener attached to the Carom meter and returns
    /// everything recorded while it ran.
    /// </summary>
    private static List<Measurement> Record(Action action)
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CaromTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements)
                measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements)
                measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();
        action();
        return measurements;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags)
            result[tag.Key] = tag.Value;
        return result;
    }

    [Fact]
    public void MeterAndActivitySourceNamesAreTheDocumentedWiringContract()
    {
        Assert.Equal("Carom", CaromTelemetry.MeterName);
        Assert.Equal("Carom", CaromTelemetry.ActivitySourceName);
    }

    [Fact]
    public void EveryAdvertisedInstrumentIsPublishedOnTheCaromMeter()
    {
        var published = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == CaromTelemetry.MeterName)
            {
                lock (published)
                    published.Add(instrument);
            }
        };

        // Touch one instrument first so the static meter definitely exists before Start replays.
        CaromTelemetry.RecordRetry(1, 0.0);
        listener.Start();

        var names = published.Select(i => i.Name).ToList();
        Assert.Contains("carom.retry.count", names);
        Assert.Contains("carom.retry.delay", names);
        Assert.Contains("carom.circuit_breaker.open.count", names);
        Assert.Contains("carom.bulkhead.rejection.count", names);
        Assert.Contains("carom.rate_limit.rejection.count", names);
        Assert.Equal("ms", published.Single(i => i.Name == "carom.retry.delay").Unit);
    }

    [Fact]
    public void RecordRetryEmitsTheCounterAndTheDelayHistogram()
    {
        var measurements = Record(() => CaromTelemetry.RecordRetry(3, 42.5, "TimeoutRejectedException"));

        var count = Assert.Single(measurements, m => m.Instrument == "carom.retry.count");
        Assert.Equal(1, count.Value);
        Assert.Equal(3, count.Tags["attempt"]);
        Assert.Equal("TimeoutRejectedException", count.Tags["exception_type"]);

        var delay = Assert.Single(measurements, m => m.Instrument == "carom.retry.delay");
        Assert.Equal(42.5, delay.Value);
    }

    [Fact]
    public void RecordRetryWithoutAnExceptionTypeTagsUnknown()
    {
        var measurements = Record(() => CaromTelemetry.RecordRetry(1, 5.0));

        var count = Assert.Single(measurements, m => m.Instrument == "carom.retry.count");
        Assert.Equal("unknown", count.Tags["exception_type"]);
    }

    [Fact]
    public void RecordCircuitBreakerOpenEmitsOneCountTaggedWithTheService()
    {
        var measurements = Record(() => CaromTelemetry.RecordCircuitBreakerOpen("payments-api"));

        var open = Assert.Single(measurements);
        Assert.Equal("carom.circuit_breaker.open.count", open.Instrument);
        Assert.Equal(1, open.Value);
        Assert.Equal("payments-api", open.Tags["service"]);
    }

    [Fact]
    public void RecordBulkheadRejectionEmitsOneCountTaggedWithTheResource()
    {
        var measurements = Record(() => CaromTelemetry.RecordBulkheadRejection("db-pool"));

        var rejection = Assert.Single(measurements);
        Assert.Equal("carom.bulkhead.rejection.count", rejection.Instrument);
        Assert.Equal(1, rejection.Value);
        Assert.Equal("db-pool", rejection.Tags["resource"]);
    }

    [Fact]
    public void RecordRateLimitRejectionEmitsOneCountTaggedWithTheService()
    {
        var measurements = Record(() => CaromTelemetry.RecordRateLimitRejection("search"));

        var rejection = Assert.Single(measurements);
        Assert.Equal("carom.rate_limit.rejection.count", rejection.Instrument);
        Assert.Equal(1, rejection.Value);
        Assert.Equal("search", rejection.Tags["service"]);
    }

    [Fact]
    public void StartActivityReturnsNullWhenNothingListens()
    {
        Assert.Null(CaromTelemetry.StartActivity("carom.shot"));
    }

    [Fact]
    public void StartActivityCreatesTheActivityWhenAListenerIsAttached()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CaromTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = CaromTelemetry.StartActivity("carom.shot", ActivityKind.Client);

        Assert.NotNull(activity);
        Assert.Equal("carom.shot", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
    }
}
