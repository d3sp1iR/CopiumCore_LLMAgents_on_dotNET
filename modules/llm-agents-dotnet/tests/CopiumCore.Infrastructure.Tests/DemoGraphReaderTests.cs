using System.Net;
using System.Text;
using CopiumCore.Application.Graph;
using CopiumCore.Infrastructure.Graph;
using Xunit;

namespace CopiumCore.Infrastructure.Tests;

public sealed class DemoGraphReaderTests
{
    private static readonly GraphReadContext Context = new("user", "trace");

    [Fact]
    public async Task ReadsObjectAndItsVersion()
    {
        using var client = CreateClient(HttpStatusCode.OK, """{"id":"req-17","type":"Application","version":"7","attributes":{"groupCode":null}}""");
        var result = await new DemoGraphReader(client).GetObjectAsync("req-17", Context, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("7", result.Version);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.Attributes.GetProperty("groupCode").ValueKind);
    }

    [Fact]
    public async Task ReturnsNullForMissingObject()
    {
        using var client = CreateClient(HttpStatusCode.NotFound, "");
        Assert.Null(await new DemoGraphReader(client).GetObjectAsync("missing", Context, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMismatchedObject()
    {
        using var client = CreateClient(HttpStatusCode.OK, """{"id":"other","type":"Application","version":"7","attributes":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => new DemoGraphReader(client).GetObjectAsync("req-17", Context, CancellationToken.None));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task RejectsIdsThatChangeTheRequestPath(string id)
    {
        using var client = CreateClient(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => new DemoGraphReader(client).GetObjectAsync(id, Context, CancellationToken.None));
    }

    [Fact]
    public async Task PropagatesServiceErrors()
    {
        using var client = CreateClient(HttpStatusCode.ServiceUnavailable, "");
        await Assert.ThrowsAsync<HttpRequestException>(() => new DemoGraphReader(client).GetObjectAsync("req-17", Context, CancellationToken.None));
    }

    private static HttpClient CreateClient(HttpStatusCode status, string body) => new(new StubHandler(status, body))
    {
        BaseAddress = new Uri("http://localhost/")
    };

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
