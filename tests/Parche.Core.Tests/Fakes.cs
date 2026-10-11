using System.Net;
using Parche.Core.Net;

namespace Parche.Core.Tests;

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public List<TimeSpan> Delays { get; } = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Handler falso: responde según la URL y guarda las peticiones.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage?> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Urls => Requests.Select(r => r.RequestUri!.AbsoluteUri).ToList();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(respond(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Ok(string fixture) => Raw(Fixture.Read(fixture));
    public static HttpResponseMessage Raw(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Ruta de la URL → fixture; la primera cuya cadena esté contenida en la URL gana.</summary>
    public HttpResponseMessage? Dispatch(HttpRequestMessage r) => respond(r);

    public static FakeHandler Routes(params (string Contains, string Fixture)[] routes) => new(r =>
    {
        var url = r.RequestUri!.AbsoluteUri;
        foreach (var (c, f) in routes) if (url.Contains(c)) return Ok(f);
        return null;
    });
}

internal static class Env
{
    public static JsonHttp Json(HttpMessageHandler handler, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return new JsonHttp(new HttpClient(handler), new RateLimiter(clock, _ => 0), new ResponseCache(clock));
    }
}
