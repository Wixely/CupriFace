using System.Net;
using System.Net.Sockets;
using System.Text;
using CupriFace.Resources;
using Xunit;

namespace CupriFace.Tests;

public class CupriSourceTests
{
    [Fact]
    public void An_explicit_empty_host_allow_list_denies_every_host()
    {
        var options = new CupriSourceOptions { AllowedHosts = [] };

        var error = Assert.Throws<CupriResourceException>(() =>
            CupriSource.Url(new Uri("https://example.com/resource"), options));

        Assert.Contains("not in AllowedHosts", error.Message);
    }

    [Fact]
    public async Task A_redirect_target_is_checked_against_the_host_allow_list_before_connecting()
    {
        var origin = new TcpListener(IPAddress.Loopback, 0);
        var blockedTarget = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        blockedTarget.Start();
        try
        {
            var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)blockedTarget.LocalEndpoint).Port;
            var target = new Uri($"http://localhost:{targetPort}/private");
            var reply = ReplyOnce(origin,
                "HTTP/1.1 302 Found\r\n" +
                $"Location: {target}\r\n" +
                "Content-Length: 0\r\nConnection: close\r\n\r\n");

            var source = CupriSource.Url(new Uri($"http://127.0.0.1:{originPort}/resource"),
                new CupriSourceOptions
                {
                    RequireHttps = false,
                    FollowRedirects = true,
                    AllowedHosts = ["127.0.0.1"],
                    Timeout = TimeSpan.FromSeconds(2),
                });

            var error = await Assert.ThrowsAsync<CupriResourceException>(() => source.ReadBytesAsync());
            Assert.Contains("host 'localhost' is not in AllowedHosts", error.Message,
                StringComparison.OrdinalIgnoreCase);
            await reply.WaitAsync(TimeSpan.FromSeconds(2));

            // Validation happens before the redirect target sees even a TCP connection.
            Assert.False(blockedTarget.Pending());
        }
        finally
        {
            origin.Stop();
            blockedTarget.Stop();
        }
    }

    [Fact]
    public async Task An_allowed_relative_redirect_is_followed()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var replies = ReplyInOrder(server,
                "HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            var source = CupriSource.Url(new Uri($"http://127.0.0.1:{port}/start"),
                new CupriSourceOptions
                {
                    RequireHttps = false,
                    FollowRedirects = true,
                    AllowedHosts = ["127.0.0.1"],
                });

            Assert.Equal("ok", Encoding.ASCII.GetString(await source.ReadBytesAsync()));
            await replies.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            server.Stop();
        }
    }

    private static async Task ReplyOnce(TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
        {
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }

    private static async Task ReplyInOrder(TcpListener listener, params string[] responses)
    {
        foreach (var response in responses)
            await ReplyOnce(listener, response);
    }
}
