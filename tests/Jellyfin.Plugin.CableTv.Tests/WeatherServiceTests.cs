using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Weather;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class WeatherServiceTests
{
    // Open-Meteo's forecast response shape (trimmed to two days and a few hours).
    private const string Forecast = """
        {"latitude":41.88,"longitude":-87.63,"timezone":"America/Chicago",
         "current":{"time":"2026-09-26T11:15","interval":900,"temperature_2m":68.4,"relative_humidity_2m":55,"apparent_temperature":67.1,
                    "weather_code":2,"wind_speed_10m":9.3,"wind_direction_10m":225,"pressure_msl":1016.2},
         "hourly":{"time":["2026-09-26T10:00","2026-09-26T11:00","2026-09-26T12:00","2026-09-26T13:00"],
                   "temperature_2m":[66.0,68.0,70.1,71.5],"weather_code":[1,2,2,3],"precipitation_probability":[0,5,10,null]},
         "daily":{"time":["2026-09-26","2026-09-27"],"weather_code":[2,61],"temperature_2m_max":[74.2,65.0],"temperature_2m_min":[58.1,52.3],
                  "precipitation_probability_max":[10,80],"sunrise":["2026-09-26T06:47","2026-09-27T06:48"],"sunset":["2026-09-26T18:44","2026-09-27T18:42"]}}
        """;

    private const string Geocoding = """{"results":[{"id":4887398,"name":"Chicago","latitude":41.85003,"longitude":-87.65005,"country_code":"US"}]}""";

    [Fact]
    public async Task ReadsTheForecastAndCachesIt()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host.StartsWith("geocoding", StringComparison.Ordinal) ? Geocoding : Forecast);
        var service = new WeatherService(new FakeFactory(handler), NullLogger<WeatherService>.Instance);
        var channel = new ChannelDefinition { Id = "w", Kind = ChannelKind.Weather, WeatherLocation = "Chicago" };

        var report = await service.GetAsync(channel, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal("Chicago", report.Location);
        Assert.Equal(68.4, report.Current.Temperature);
        Assert.Equal("SW", report.Current.WindDirection);
        Assert.Equal("Partly cloudy", report.Current.Condition);
        Assert.Equal(30.01, report.Current.Pressure);
        Assert.Equal(2, report.Daily.Count);
        Assert.Equal("Lt Rain", report.Daily[1].Condition);
        Assert.Equal(80, report.Daily[1].PrecipitationChance);
        Assert.Equal("2026-09-26T11:00", report.Hourly[0].Time);
        Assert.Null(report.Hourly[2].PrecipitationChance);
        Assert.Equal("06:47", report.Sunrise);
        Assert.Contains(handler.Requests, u => u.Contains("latitude=41.85003", StringComparison.Ordinal) && u.Contains("fahrenheit", StringComparison.Ordinal));

        await service.GetAsync(channel, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count); // geocode + forecast once; the second call is cached
    }

    [Fact]
    public async Task CoordinatesSkipTheGeocoderAndErrorsGiveNull()
    {
        var handler = new FakeHandler(_ => """{"reason":"Daily API request limit exceeded.","error":true}""");
        var service = new WeatherService(new FakeFactory(handler), NullLogger<WeatherService>.Instance);
        var channel = new ChannelDefinition { Id = "w", Kind = ChannelKind.Weather, WeatherLocation = "41.88,-87.63", WeatherMetric = true };

        Assert.Null(await service.GetAsync(channel, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("fahrenheit", handler.Requests[0], StringComparison.Ordinal);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
        }
    }
}
