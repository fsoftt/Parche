using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
namespace Parche.Core.Net;

/// <summary>Reloj y espera inyectables para que los tests no duerman.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}

/// <summary>Política de cortesía por servicio: intervalo, simultaneidad, caché y reintentos.</summary>
public sealed class RateLimitSpec
{
    /// <summary>Milisegundos mínimos entre el inicio de dos peticiones al mismo host.</summary>
    public int MinIntervalMs { get; set; } = 300;
    /// <summary>Retardo aleatorio adicional 0..JitterMs.</summary>
    public int JitterMs { get; set; }
    /// <summary>Peticiones simultáneas máximas por host.</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Segundos que se cachea en memoria la respuesta por URL (0 = sin caché).</summary>
    public int CacheSeconds { get; set; } = 600;
    /// <summary>Reintentos ante HTTP 429/503.</summary>
    public int MaxRetries { get; set; } = 2;
    /// <summary>Espera base del retroceso exponencial (backoffMs * 2^n) si no hay Retry-After.</summary>
    public int BackoffMs { get; set; } = 2000;
}

/// <summary>
/// Limita las peticiones por host: separa los inicios al menos <c>minIntervalMs</c> (+ jitter) y
/// acota las simultáneas. Compartido por todos los fetchers del proceso (<see cref="Shared"/>).
/// </summary>
public sealed class RateLimiter(IClock? clock = null, Func<int, int>? jitter = null)
{
    public static readonly RateLimiter Shared = new();

    private sealed class HostState(int maxConcurrent)
    {
        public readonly SemaphoreSlim Gate = new(Math.Max(1, maxConcurrent));
        public readonly object Lock = new();
        public DateTime NextStart = DateTime.MinValue;
    }

    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly Func<int, int> _jitter = jitter ?? (max => max <= 0 ? 0 : Random.Shared.Next(0, max + 1));
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public IClock Clock => _clock;

    /// <summary>Espera el turno para pedir <paramref name="url"/>; liberar el resultado al terminar la petición.</summary>
    public async Task<IDisposable> AcquireAsync(string url, RateLimitSpec spec, CancellationToken ct)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        var state = _hosts.GetOrAdd(host, _ => new HostState(spec.MaxConcurrent));
        await state.Gate.WaitAsync(ct);
        try
        {
            TimeSpan wait;
            lock (state.Lock)
            {
                var now = _clock.UtcNow;
                var start = state.NextStart > now ? state.NextStart : now;
                state.NextStart = start.AddMilliseconds(Math.Max(0, spec.MinIntervalMs) + _jitter(Math.Max(0, spec.JitterMs)));
                wait = start - now;
            }
            if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, ct);
        }
        catch
        {
            state.Gate.Release();
            throw;
        }
        return new Releaser(state.Gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}

/// <summary>Caché en memoria por URL (JSON de catálogos y metadatos; nunca listas de streams).</summary>
public sealed class ResponseCache(IClock? clock = null)
{
    public static readonly ResponseCache Shared = new();

    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly ConcurrentDictionary<string, (DateTime At, string Body)> _items = new();

    public string? Get(string key, int seconds)
    {
        if (seconds <= 0) return null;
        if (_items.TryGetValue(key, out var e) && _clock.UtcNow - e.At < TimeSpan.FromSeconds(seconds)) return e.Body;
        return null;
    }

    public void Set(string key, string body, int seconds)
    {
        if (seconds > 0) _items[key] = (_clock.UtcNow, body);
    }
}

public sealed class SiteRateLimitedException(int status)
    : Exception($"El servicio limitó las solicitudes ({status}). Intenta de nuevo en unos segundos.")
{
    public int Status { get; } = status;
}

/// <summary>GET con limitación por host y reintentos ante 429/503 (respeta Retry-After).</summary>
public static class PoliteHttp
{
    public static async Task<string> GetStringAsync(HttpClient http, RateLimiter limiter, RateLimitSpec rl, string url,
        Func<HttpRequestMessage> makeRequest, int timeoutMs, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var slot = await limiter.AcquireAsync(url, rl, ct);
            using var req = makeRequest();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            using var res = await http.SendAsync(req, timeout.Token);

            if (res.StatusCode is (HttpStatusCode)429 or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt >= rl.MaxRetries) throw new SiteRateLimitedException((int)res.StatusCode);
                var delay = RetryAfter(res.Headers.RetryAfter, limiter.Clock.UtcNow)
                    ?? TimeSpan.FromMilliseconds(Math.Max(0, rl.BackoffMs) * Math.Pow(2, attempt));
                slot.Dispose();
                await limiter.Clock.DelayAsync(delay, ct);
                continue;
            }

            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(timeout.Token);
        }
    }

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? h, DateTime now)
    {
        if (h is null) return null;
        if (h.Delta is { } d) return d;
        if (h.Date is { } date) return date.UtcDateTime > now ? date.UtcDateTime - now : TimeSpan.Zero;
        return null;
    }
}
