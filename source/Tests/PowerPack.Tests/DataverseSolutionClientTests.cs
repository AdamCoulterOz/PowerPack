using System.Net;
using System.Text;
using Azure.Core;
using PowerPack.Services;

namespace PowerPack.Tests;

public sealed class DataverseSolutionClientTests
{
    [Fact]
    public async Task SetSolutionVersionAsyncUpdatesVersionAndVerifiesIt()
    {
        var solutionId = Guid.NewGuid();
        var handler = new QueueMessageHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Contains("/api/data/v9.2/solutions?", request.RequestUri!.PathAndQuery);
                return Json($$"""
                    {
                      "value": [
                        {
                          "solutionid": "{{solutionId}}",
                          "uniquename": "Core",
                          "version": "1.5.78"
                        }
                      ]
                    }
                    """);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal($"/api/data/v9.2/solutions({solutionId})", request.RequestUri!.PathAndQuery);
                Assert.Equal("""{"version":"1.5.79"}""", request.Content!.ReadAsStringAsync().Result);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Json($$"""
                    {
                      "value": [
                        {
                          "solutionid": "{{solutionId}}",
                          "uniquename": "Core",
                          "version": "1.5.79"
                        }
                      ]
                    }
                    """);
            });

        var client = new DataverseSolutionClient(new HttpClient(handler), new StaticTokenCredential());
        await client.SetSolutionVersionAsync("https://example.crm.dynamics.com/", "Core", "1.5.79");

        Assert.True(handler.Drained);
    }

    [Fact]
    public async Task ExportSolutionAsyncUsesAsyncExportAndDownloadsResult()
    {
        var asyncOperationId = Guid.NewGuid();
        var exportJobId = Guid.NewGuid();
        var expected = Encoding.UTF8.GetBytes("solution zip");
        var handler = new QueueMessageHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/data/v9.2/ExportSolutionAsync", request.RequestUri!.PathAndQuery);
                Assert.Equal("""{"SolutionName":"Core","Managed":true}""", request.Content!.ReadAsStringAsync().Result);
                return Json($$"""
                    {
                      "AsyncOperationId": "{{asyncOperationId}}",
                      "ExportJobId": "{{exportJobId}}"
                    }
                    """);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"/api/data/v9.2/asyncoperations({asyncOperationId})?$select=statecode,statuscode,errorcode,message,messagename,friendlymessage,correlationid", request.RequestUri!.PathAndQuery);
                return Json("""{"statecode":3,"statuscode":30}""");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/data/v9.2/DownloadSolutionExportData", request.RequestUri!.PathAndQuery);
                Assert.Equal($$"""{"ExportJobId":"{{exportJobId}}"}""", request.Content!.ReadAsStringAsync().Result);
                return Json($$"""{"ExportSolutionFile":"{{Convert.ToBase64String(expected)}}"}""");
            });

        var client = new DataverseSolutionClient(new HttpClient(handler), new StaticTokenCredential());
        var actual = await client.ExportSolutionAsync(
            "https://example.crm.dynamics.com/",
            "Core",
            managed: true,
            useAsync: true,
            maxAsyncWaitTime: TimeSpan.FromSeconds(1));

        Assert.Equal(expected, actual);
        Assert.True(handler.Drained);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class QueueMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public bool Drained => _responses.Count == 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            Assert.NotEmpty(_responses);
            var response = _responses.Dequeue();
            return Task.FromResult(response(request));
        }
    }
}
