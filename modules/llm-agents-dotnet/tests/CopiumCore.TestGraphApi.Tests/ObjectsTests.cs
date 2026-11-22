using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopiumCore.TestGraphApi.Tests;

public sealed class ObjectsTests
{
    [Theory]
    [InlineData("req-17", "Application", "7")]
    [InlineData("doc-9", "Decision", "3")]
    public async Task ReturnsVersionedFixtureOverHttp(string id, string type, string version)
    {
        await using var app = TestGraphApp.Build([]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var response = await client.GetAsync($"/objects/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.Equal(type, root.GetProperty("type").GetString());
        Assert.Equal(version, root.GetProperty("version").GetString());
        var attributes = root.GetProperty("attributes");
        Assert.Equal(JsonValueKind.Object, attributes.ValueKind);
        if (id == "req-17")
        {
            Assert.Equal(JsonValueKind.Null, attributes.GetProperty("groupCode").ValueKind);
            Assert.Equal("project-4", attributes.GetProperty("projectId").GetString());
            Assert.Equal("doc-9", attributes.GetProperty("decisionId").GetString());
        }
        else
        {
            Assert.Equal("req-17", attributes.GetProperty("applicationId").GetString());
            Assert.Equal("ИУ5-31Б", attributes.GetProperty("groupCode").GetString());
            Assert.True(attributes.GetProperty("approved").GetBoolean());
        }
    }

    [Fact]
    public async Task CasWriteRejectsStaleVersionAndReplaysOperation()
    {
        await using var app = TestGraphApp.Build([]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var request = new
        {
            value = "ИУ5-31Б",
            expectedVersion = "7",
            operationId = "op-cas-test"
        };
        var applied = await client.PutAsJsonAsync("/objects/req-17/attributes/groupCode", request);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var receipt = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("8", receipt.GetProperty("version").GetString());
        Assert.True(receipt.GetProperty("applied").GetBoolean());

        var replay = await client.PutAsJsonAsync("/objects/req-17/attributes/groupCode", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("8", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

        var stale = await client.PutAsJsonAsync("/objects/req-17/attributes/groupCode", new
        {
            value = "ИУ5-32Б",
            expectedVersion = "7",
            operationId = "op-cas-stale"
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task UnknownObjectReturnsNotFound()
    {
        await using var app = TestGraphApp.Build([]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var response = await client.GetAsync("/objects/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
