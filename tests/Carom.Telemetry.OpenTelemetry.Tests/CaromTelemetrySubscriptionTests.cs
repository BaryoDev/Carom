using System.Diagnostics.Metrics;
using Xunit;
using Carom.Extensions;
using Carom.Telemetry.OpenTelemetry;

namespace Carom.Telemetry.OpenTelemetry.Tests;

/// <summary>
/// Proves the CaromHooks seam actually drives the instruments end to end: real
/// retries, circuit opens, bulkhead and rate limit rejections reach a MeterListener
/// on the Carom meter once Subscribe is called. Subscribe mutates process-wide
/// hooks, so every test unsubscribes in a finally block, all subscription tests
/// live in this class, and the class shares the "CaromMeter" collection with
/// CaromTelemetryTests so nothing else reads the meter while these fire. Signals
/// are attributed by GUID keys or a marker exception type, never by counting
/// everything on the meter.
/// </summary>
[CollectionDefinition("CaromMeter")]
public class CaromMeterCollection
{
}

[Collection("CaromMeter")]
public class CaromTelemetrySubscriptionTests
{
    private sealed class TelemetryProbeException : Exception
    {
    }

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

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
    public void AfterSubscribe_AFailingRetryRecordsOnTheCaromMeter()
    {
        try
        {
            CaromTelemetry.Subscribe();

            var measurements = Record(() =>
            {
                int calls = 0;
                global::Carom.Carom.Shot(
                    () =>
                    {
                        calls++;
                        if (calls == 1) throw new TelemetryProbeException();
                        return 7;
                    },
                    retries: 1,
                    baseDelay: TimeSpan.FromMilliseconds(1),
                    shouldBounce: null,
                    disableJitter: true);
            });

            var retry = Assert.Single(measurements, m =>
                m.Instrument == "carom.retry.count"
                && Equals(m.Tags["exception_type"], nameof(TelemetryProbeException)));
            Assert.Equal(1, retry.Value);
            Assert.Equal(1, retry.Tags["attempt"]);
        }
        finally
        {
            CaromTelemetry.Unsubscribe();
        }
    }

    [Fact]
    public void AfterSubscribe_ACircuitOpenRecordsOnTheCaromMeter()
    {
        var key = "telemetry-circuit-" + Guid.NewGuid();
        try
        {
            CaromTelemetry.Subscribe();

            var measurements = Record(() =>
            {
                var cushion = Cushion.ForService(key)
                    .OpenAfter(2, 10)
                    .HalfOpenAfter(TimeSpan.FromHours(1));

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        CaromCushionExtensions.Shot<int>(
                            static () => throw new InvalidOperationException("boom"),
                            cushion,
                            retries: 0);
                    }
                    catch (InvalidOperationException) { }
                    catch (CircuitOpenException) { }
                }
            });

            var open = Assert.Single(measurements, m =>
                m.Instrument == "carom.circuit_breaker.open.count" && Equals(m.Tags["service"], key));
            Assert.Equal(1, open.Value);
        }
        finally
        {
            CaromTelemetry.Unsubscribe();
        }
    }

    [Fact]
    public void AfterSubscribe_ARateLimitRejectionRecordsOnTheCaromMeter()
    {
        var key = "telemetry-throttle-" + Guid.NewGuid();
        try
        {
            CaromTelemetry.Subscribe();

            var measurements = Record(() =>
            {
                var throttle = Throttle.ForService(key)
                    .WithRate(1, TimeSpan.FromHours(1))
                    .WithBurst(1)
                    .Build();

                CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
                Assert.Throws<ThrottledException>(() =>
                    CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0));
            });

            var rejection = Assert.Single(measurements, m =>
                m.Instrument == "carom.rate_limit.rejection.count" && Equals(m.Tags["service"], key));
            Assert.Equal(1, rejection.Value);
        }
        finally
        {
            CaromTelemetry.Unsubscribe();
        }
    }

    [Fact]
    public async Task AfterSubscribe_ABulkheadRejectionRecordsOnTheCaromMeter()
    {
        var key = "telemetry-bulkhead-" + Guid.NewGuid();
        var compartment = Compartment.ForResource(key)
            .WithMaxConcurrency(1)
            .Build();

        using var release = new SemaphoreSlim(0);
        using var entered = new ManualResetEventSlim(false);

        // Hold the single slot from another thread.
        var holder = Task.Run(() =>
            CaromCompartmentExtensions.Shot<int>(
                () => { entered.Set(); release.Wait(); return 1; },
                compartment,
                retries: 0));

        try
        {
            CaromTelemetry.Subscribe();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Holder never entered the compartment.");

            var measurements = Record(() =>
            {
                Assert.Throws<CompartmentFullException>(() =>
                    CaromCompartmentExtensions.Shot(() => 2, compartment, retries: 0));
            });

            var rejection = Assert.Single(measurements, m =>
                m.Instrument == "carom.bulkhead.rejection.count" && Equals(m.Tags["resource"], key));
            Assert.Equal(1, rejection.Value);
        }
        finally
        {
            CaromTelemetry.Unsubscribe();
            release.Release();
            await holder;
        }
    }

    [Fact]
    public void SubscribingTwice_RecordsEachSignalOnce()
    {
        var key = "telemetry-twice-" + Guid.NewGuid();
        try
        {
            CaromTelemetry.Subscribe();
            CaromTelemetry.Subscribe();

            var measurements = Record(() =>
            {
                var throttle = Throttle.ForService(key)
                    .WithRate(1, TimeSpan.FromHours(1))
                    .WithBurst(1)
                    .Build();

                CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
                Assert.Throws<ThrottledException>(() =>
                    CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0));
            });

            Assert.Single(measurements, m =>
                m.Instrument == "carom.rate_limit.rejection.count" && Equals(m.Tags["service"], key));
        }
        finally
        {
            CaromTelemetry.Unsubscribe();
        }
    }

    [Fact]
    public void AfterUnsubscribe_NothingIsRecorded()
    {
        var key = "telemetry-unsub-" + Guid.NewGuid();
        CaromTelemetry.Subscribe();
        CaromTelemetry.Unsubscribe();
        CaromTelemetry.Unsubscribe();

        var measurements = Record(() =>
        {
            var throttle = Throttle.ForService(key)
                .WithRate(1, TimeSpan.FromHours(1))
                .WithBurst(1)
                .Build();

            CaromThrottleExtensions.Shot(() => 1, throttle, retries: 0);
            Assert.Throws<ThrottledException>(() =>
                CaromThrottleExtensions.Shot(() => 2, throttle, retries: 0));
        });

        Assert.DoesNotContain(measurements, m =>
            m.Instrument == "carom.rate_limit.rejection.count" && Equals(m.Tags["service"], key));
    }
}
