using System.Net;
using Parche.Core.Net;

namespace Parche.Core.Tests;

public class RateLimiterTests
{
    private static RateLimitSpec Spec(int interval = 1000, int concurrent = 2, int retries = 2) =>
        new() { MinIntervalMs = interval, JitterMs = 0, MaxConcurrent = concurrent, CacheSeconds = 60, MaxRetries = retries, BackoffMs = 2000 };

    [Fact]
    public async Task Spaces_request_starts_per_host()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock, _ => 0);
        var t0 = clock.UtcNow;
        for (var i = 0; i < 3; i++) (await limiter.AcquireAsync("https://a.test/x", Spec(), default)).Dispose();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.UtcNow - t0);
        Assert.Equal(2, clock.Delays.Count);
    }

    [Fact]
    public async Task Jitter_is_added_to_the_interval()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock, max => max);
        var spec = Spec(); spec.JitterMs = 400;
        (await limiter.AcquireAsync("https://a.test/", spec, default)).Dispose();
        (await limiter.AcquireAsync("https://a.test/", spec, default)).Dispose();
        Assert.Equal(TimeSpan.FromMilliseconds(1400), clock.Delays.Single());
    }

    [Fact]
    public async Task Hosts_are_independent()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock, _ => 0);
        (await limiter.AcquireAsync("https://a.test/", Spec(), default)).Dispose();
        (await limiter.AcquireAsync("https://b.test/", Spec(), default)).Dispose();
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task Concurrency_is_capped_per_host()
    {
        var limiter = new RateLimiter(new FakeClock(), _ => 0);
        var spec = Spec(0, 2);
        var a = await limiter.AcquireAsync("https://a.test/", spec, default);
        var b = await limiter.AcquireAsync("https://a.test/", spec, default);
        var third = limiter.AcquireAsync("https://a.test/", spec, default);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        var other = await limiter.AcquireAsync("https://b.test/", spec, default);
        a.Dispose();
        (await third).Dispose();
        b.Dispose(); other.Dispose();
    }

    [Fact]
    public async Task Cache_hit_skips_the_network()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler(_ => FakeHandler.Raw("{\"a\":1}"));
        var json = Env.Json(handler, clock);
        var spec = Spec(0);
        await json.GetAsync("https://a.test/p", spec);
        await json.GetAsync("https://a.test/p", spec);
        Assert.Single(handler.Requests);

        await clock.DelayAsync(TimeSpan.FromSeconds(61), default);
        await json.GetAsync("https://a.test/p", spec);
        Assert.Equal(2, handler.Requests.Count);

        spec.CacheSeconds = 0;
        await json.GetAsync("https://a.test/p", spec);
        await json.GetAsync("https://a.test/p", spec);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_429_honoring_retry_after_then_succeeds()
    {
        var clock = new FakeClock();
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            if (calls++ > 0) return FakeHandler.Raw("{}");
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return r;
        });
        var body = await Env.Json(handler, clock).GetAsync("https://a.test/api", Spec(0));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, body.ValueKind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(3), clock.Delays);
    }

    [Fact]
    public async Task Uses_exponential_backoff_and_then_throws_clear_error()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ex = await Assert.ThrowsAsync<SiteRateLimitedException>(() => Env.Json(handler, clock).GetAsync("https://a.test/p", Spec(0, 2, 2)));
        Assert.Equal("El servicio limitó las solicitudes (503). Intenta de nuevo en unos segundos.", ex.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, clock.Delays);
    }

    [Fact]
    public async Task Http_errors_surface_with_status_code()
    {
        var handler = new FakeHandler(_ => FakeHandler.Raw("{}", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Env.Json(handler).GetAsync("https://a.test/p", Spec(0)));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}
