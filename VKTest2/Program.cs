using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var cities = File.ReadAllLines("Cities.txt")
    .Select(s => s.Trim())
    .Where(s => !string.IsNullOrWhiteSpace(s))
    .Distinct()
    .ToList();

var handler = new SocketsHttpHandler
{
    ConnectTimeout = TimeSpan.FromSeconds(15),
    PooledConnectionLifetime = TimeSpan.FromMinutes(1),
    AutomaticDecompression = DecompressionMethods.None, 
    ConnectCallback = async (context, token) =>
    {
        var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, token);
        var ipv4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? throw new Exception("No IPv4 address");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(ipv4, context.DnsEndPoint.Port), token);
        return new NetworkStream(socket, ownsSocket: true);
    }
};

using var client = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(60)
};
client.DefaultRequestHeaders.Add("User-Agent", "curl");
client.DefaultRequestHeaders.Add("Accept", "application/json");
client.DefaultRequestHeaders.Add("Accept-Encoding", "identity");

var results = new List<WeatherData>();

foreach (var city in cities)
{
    try
    {
        var data = await FetchAsync(client, city);
        if (data == null)
        {
            continue;
        }
        results.Add(data);
        Console.WriteLine($"{data.City}, {data.Country} {data.TempC:+0;-0;0} °C");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{city}: {ex.GetType().Name}: {ex.Message}");
    }

    await Task.Delay(300);
}

Console.WriteLine("\n─────── Статистика по странам ───────");

var byCountry = results
    .GroupBy(r => r.Country)
    .Select(g => new
    {
        Country = g.Key,
        Count = g.Count(),
        Avg = g.Average(x => x.TempC),
        Min = g.Min(x => x.TempC),
        Max = g.Max(x => x.TempC)
    })
    .OrderByDescending(x => x.Count)
    .ThenBy(x => x.Country);

foreach (var s in byCountry)
{
    Console.WriteLine(
        $"{s.Country} — {s.Count} cities, " +
        $"avg: {s.Avg:+0;-0;0} °C, " +
        $"min: {s.Min:+0;-0;0} °C, " +
        $"max: {s.Max:+0;-0;0} °C");
}

static async Task<WeatherData?> FetchAsync(HttpClient client, string city)
{
    var url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1";
    //Console.WriteLine($"[debug] GET {url}");

    using var request = new HttpRequestMessage(HttpMethod.Get, url)
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    using var response = await client.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead);
    //Console.WriteLine($"[debug] status: {(int)response.StatusCode}");
    response.EnsureSuccessStatusCode();

    using var stream = await response.Content.ReadAsStreamAsync();

    var markers = new[]
    {
        Encoding.UTF8.GetBytes("\"request\":"),
        Encoding.UTF8.GetBytes("\"weather\":")
    };

    var ms = new MemoryStream();
    var buf = new byte[16 * 1024];
    int markerBytePos = -1;

    while (true)
    {
        int n = await stream.ReadAsync(buf.AsMemory());
        //Console.WriteLine($"[debug] {n} bytes (total {ms.Length + n})");
        if (n == 0) break;

        ms.Write(buf, 0, n);

        var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        int best = -1;
        foreach (var m in markers)
        {
            int p = data.IndexOf(m);
            if (p >= 0 && (best < 0 || p < best)) best = p;
        }

        if (best >= 0)
        {
            markerBytePos = best;
            //Console.WriteLine($"[debug] {best}");
            break;
        }
    }

    if (ms.Length == 0) return null;

    int headLen = markerBytePos > 0 ? markerBytePos : (int)ms.Length;
    var head = Encoding.UTF8.GetString(ms.GetBuffer(), 0, headLen);

    int cut = head.Length;
    while (cut > 0)
    {
        char c = head[cut - 1];
        if (char.IsWhiteSpace(c) || c == ',') cut--;
        else break;
    }

    var text = head.Substring(0, cut) + "}";

    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var weather = JsonSerializer.Deserialize<WttrResponse>(text, options);

    var current = weather?.CurrentCondition?.FirstOrDefault();
    var area = weather?.NearestArea?.FirstOrDefault();
    if (current == null || area == null) return null;
    if (string.IsNullOrEmpty(current.TempC)) return null;

    if (!double.TryParse(current.TempC, NumberStyles.Any,
            CultureInfo.InvariantCulture, out var temp))
        return null;

    return new WeatherData
    {
        City = area.AreaName?.FirstOrDefault()?.Value ?? city,
        Country = area.Country?.FirstOrDefault()?.Value ?? "Unknown",
        TempC = temp
    };
}

public class WeatherData
{
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public double TempC { get; set; }
}

public class WttrResponse
{
    [JsonPropertyName("current_condition")]
    public List<CurrentCondition>? CurrentCondition { get; set; }

    [JsonPropertyName("nearest_area")]
    public List<NearestArea>? NearestArea { get; set; }
}

public class CurrentCondition
{
    [JsonPropertyName("temp_C")] public string? TempC { get; set; }
}

public class NearestArea
{
    [JsonPropertyName("areaName")] public List<ValueWrapper>? AreaName { get; set; }
    [JsonPropertyName("country")] public List<ValueWrapper>? Country { get; set; }
}

public class ValueWrapper
{
    [JsonPropertyName("value")] public string? Value { get; set; }
}