# Circuit breaker (Cushion)

`Cushion` is Carom's circuit breaker. It lives in `Carom.Extensions`.

## Keys

A breaker is identified by its service key, `Cushion.ForService("payment-api")`. The state
(closed, open, half-open, and the recent outcomes) is stored per key in one process-wide
store. Two `Cushion` values with the same key share one breaker.

`Cushion.GetState(key)` reads the state without creating it. It returns null if the key
has no breaker yet.

## When versus ShouldTrip

`When(predicate)` on the builder sets `ShouldTrip`. It decides which exceptions count as the
dependency's fault. An exception the predicate rejects is rethrown and the circuit does not
see it, so a bug in your calling code cannot open the circuit for a healthy service.

The default counts everything except `OperationCanceledException`, with one exception to
that: `TimeoutRejectedException` (a `Bounce.WithTimeout` timeout) counts.

`ShouldBounce` on a `Bounce` is a different question: it decides whether to retry. The two
are independent.

## Using the breaker on its own

`ExecuteAsync` and `Execute` are public and do not retry:

```csharp
var result = await cushion.ExecuteAsync(ct => client.GetAsync(url, ct), ct);
var other = cushion.Execute(() => LoadFromDisk());
```

The token overload hands the action the token you pass in. The breaker never cancels it.

## Composing with a Bounce

`CaromCushionExtensions.ShotAsync` runs the Bounce inside the breaker: one logical call
records one outcome, however many attempts it took. A call that arrives at an open circuit
fails fast and is not retried.

Use the overload that takes `Func<CancellationToken, Task<T>>` when the Bounce has a timeout.
The token is cancelled when the timeout fires, so the call stops instead of running on after
the breaker has given up on it:

```csharp
var bounce = Bounce.Times(2).WithDelay(TimeSpan.FromMilliseconds(200)).WithTimeout(TimeSpan.FromSeconds(2));
var order = await CaromCushionExtensions.ShotAsync(ct => api.GetOrderAsync(id, ct), cushion, bounce);
```

The older `Func<Task<T>>` overloads still work. A lambda that ignores the token, `() => ...`,
picks them. A lambda that takes one parameter, `ct => ...`, picks the token overloads.

## The conflict rule

A key holds one set of settings. Registering a key again with a different failure threshold,
sampling window, half-open delay or sampling duration throws `InvalidOperationException`.
`When` and the clock are not compared.

This is a decision to keep, not a gap: if the store were keyed by key and settings, the same
service name could hold two breakers and `GetState(key)` would have no single answer. If a
process needs two configurations, give them two keys. In tests, call `Cushion.Reset(key)`
between configurations.

## Eviction and the cap

The store keeps at most 1000 breakers. Set `Cushion.MaxBreakers` once at startup to change
it (minimum 1). When a new key pushes the store over the cap, the least recently used
breakers are evicted, about 10% of the cap beyond the overflow. A breaker touched during the
scan is skipped.

An evicted breaker is rebuilt closed on its next call. If it was open, the circuit silently
resets. Set the cap above the number of distinct keys you expect, and keep keys to a bounded
set (service names, not request ids).

## Reset

`Cushion.Reset(key)` forgets a breaker. The next call starts a new closed breaker with the
settings that call registers. It returns false when nothing was stored.

## Testing without sleeping

`WithClock(Func<long>)` replaces the clock. It returns a monotonic timestamp in
`Stopwatch.Frequency` units per second:

```csharp
long now = Stopwatch.GetTimestamp();
var cushion = Cushion.ForService("t").OpenAfter(1, 1).WithClock(() => now)
    .HalfOpenAfter(TimeSpan.FromSeconds(30));
// open the circuit, then:
now += 31 * Stopwatch.Frequency;   // the next call is the half-open probe
```
