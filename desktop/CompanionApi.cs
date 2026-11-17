using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TallyPager;

internal sealed record ApiRequest(Guid ProfileId, Guid[]? Roles, bool Chat, bool Next);
internal sealed record ApiCommand(string Action, Guid TextId, ApiRequest? Body);
internal sealed record ApiReply(int Status, object Value)
{
    public static ApiReply Error(int status, string error) => new(status, new { error });
}

internal sealed class CompanionApi : IDisposable
{
    public const int Port = 46225;
    public const string Address = "http://127.0.0.1:46225";
    private readonly WebApplication app;
    private readonly byte[] authorization;
    private readonly Func<ApiCommand, CancellationToken, Task<ApiReply>> dispatch;
    private readonly SemaphoreSlim mutations = new(1);
    private readonly Dictionary<string, (string Fingerprint, ApiReply Reply)> saved = [];
    private readonly Queue<string> ids = new();

    public CompanionApi(string token, Func<ApiCommand, CancellationToken, Task<ApiReply>> dispatch, int port = Port)
    {
        authorization = Encoding.ASCII.GetBytes("Bearer " + token);
        this.dispatch = dispatch;
        var builder = WebApplication.CreateEmptyBuilder(new());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 16_384;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(IPAddress.Loopback, port);
        });
        builder.Services.AddRoutingCore();
        app = builder.Build();
        app.Run(Handle);
    }

    public Task StartAsync() => app.StartAsync();

    private async Task Handle(HttpContext context)
    {
        var request = context.Request;
        string credential = request.Headers.Authorization.ToString();
        ApiReply reply;
        if (!CryptographicOperations.FixedTimeEquals(authorization, Encoding.UTF8.GetBytes(credential)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            reply = ApiReply.Error(401, "Bearer-Token erforderlich.");
        }
        else
        {
            try { reply = await Route(request, context.RequestAborted); }
            catch (Exception error) when (error is JsonException or BadHttpRequestException)
            {
                reply = ApiReply.Error(400, "Ungültiger JSON-Request.");
            }
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = reply.Status;
        await context.Response.WriteAsJsonAsync(reply.Value, context.RequestAborted);
    }

    private async Task<ApiReply> Route(HttpRequest request, CancellationToken cancel)
    {
        string[] parts = request.Path.Value?.Split('/') ?? [];
        if (HttpMethods.IsGet(request.Method) && parts is ["", "api", "v1", "status"])
            return await dispatch(new("status", Guid.Empty, null), cancel);
        if (!HttpMethods.IsPost(request.Method)) return ApiReply.Error(404, "Endpunkt fehlt.");

        ApiCommand command;
        if (parts is ["", "api", "v1", "quick-texts", var text, "send"] && Guid.TryParseExact(text, "D", out Guid id))
        {
            command = new("send", id, null);
        }
        else if (parts is ["", "api", "v1", "messages", "clear"])
        {
            var body = await JsonSerializer.DeserializeAsync<ApiRequest>(request.Body, ProfileStore.Json, cancel);
            if (body is null || body.ProfileId == Guid.Empty || body.Roles is not { Length: > 0 and <= 200 } ||
                body.Roles.Contains(Guid.Empty) || !body.Chat && !body.Next)
                return ApiReply.Error(400, "Profil-ID, Kamera-Rollen und mindestens ein Feld angeben.");
            command = new("clear", Guid.Empty, body);
        }
        else return ApiReply.Error(404, "Endpunkt fehlt.");

        string key = request.Headers["Idempotency-Key"].ToString();
        if (key.Length == 0) return await dispatch(command, cancel);
        if (key.Length > 96 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            return ApiReply.Error(400, "Ungültiger Idempotency-Key.");
        string fingerprint = JsonSerializer.Serialize(command);
        await mutations.WaitAsync(cancel);
        try
        {
            if (saved.TryGetValue(key, out var previous))
                return previous.Fingerprint == fingerprint ? previous.Reply :
                    ApiReply.Error(409, "Dieser Idempotency-Key wurde für eine andere Aktion verwendet.");
            // Finish an accepted action even if the caller disconnects before reading the result.
            var reply = await dispatch(command, CancellationToken.None);
            if (reply.Status is >= 200 and < 300)
            {
                saved[key] = (fingerprint, reply);
                ids.Enqueue(key);
                if (ids.Count > 256) saved.Remove(ids.Dequeue());
            }
            return reply;
        }
        finally { mutations.Release(); }
    }

    public void Dispose() => _ = CloseAsync();

    public async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
}
