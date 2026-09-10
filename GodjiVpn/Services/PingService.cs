using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodjiVpn.Models;

namespace GodjiVpn.Services;

/// <summary>
/// Настоящий пинг через прокси-протокол — аналог PingRepository.kt (Android): вместо голого
/// TCP-connect до host:port поднимаем ВРЕМЕННЫЙ xray.exe с профилем узла как есть и меряем
/// реальную задержку HTTP-запроса через получившийся SOCKS5 (включая оверхед самого
/// VLESS+Reality-туннеля, а не только TCP-рукопожатие до порта сервера). Android вынужден
/// сериализовать такие проверки через Mutex — общее состояние одной нативной Go-библиотеки на
/// весь процесс. Здесь этого ограничения нет: каждый временный xray.exe — отдельный ОС-процесс
/// со своей памятью, поэтому проверки узлов идут по-настоящему параллельно (ограничено только
/// Concurrency ниже — чтобы не поднимать десятки процессов разом).
/// </summary>
public sealed class PingService
{
    private static readonly SemaphoreSlim Concurrency = new(3);

    private static string RuntimeDir => Path.Combine(AppContext.BaseDirectory, "Runtime");
    private static string TempDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "ping-tmp");

    private readonly PingSettings _settings;

    public PingService(PingSettings settings) => _settings = settings;

    /// <returns>Задержка в мс, или -1, если узел недоступен (сервер не ответил вовремя,
    /// xray.exe не поднялся, HTTP-запрос через тоннель не прошёл и т.п.).</returns>
    public async Task<int> MeasureAsync(VlessNode node, CancellationToken ct = default)
    {
        var method = _settings.Method;
        // TCP/ICMP не смотрят на JSON-конфиг узла вообще — только host:port, без прокси и без
        // временного xray.exe (см. PingRepository.kt — тот же выбор: эти методы быстрее и не
        // требуют поднимать процесс, но не отражают работоспособность самого VLESS-протокола).
        if (method is PingMethod.Tcp or PingMethod.Icmp)
            return await MeasureDirectAsync(node, method, ct).ConfigureAwait(false);

        await Concurrency.WaitAsync(ct).ConfigureAwait(false);
        try { return await MeasureCoreAsync(node, method, _settings.TestUrl, ct).ConfigureAwait(false); }
        finally { Concurrency.Release(); }
    }

    private static async Task<int> MeasureDirectAsync(VlessNode node, PingMethod method, CancellationToken ct)
    {
        try
        {
            if (method == PingMethod.Icmp)
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(node.Host, 2000).ConfigureAwait(false);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? (int)reply.RoundtripTime : -1;
            }

            var sw = Stopwatch.StartNew();
            using var socket = new TcpClient();
            var connectTask = socket.ConnectAsync(node.Host, node.Port);
            if (await Task.WhenAny(connectTask, Task.Delay(2000, ct)).ConfigureAwait(false) != connectTask || !socket.Connected)
                return -1;
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    private static async Task<int> MeasureCoreAsync(VlessNode node, PingMethod method, string probeUrl, CancellationToken ct)
    {
        Directory.CreateDirectory(TempDir);
        var port = GetFreeTcpPort();
        var configPath = Path.Combine(TempDir, $"ping-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            File.WriteAllText(configPath, BuildConfig(node, port));

            var exePath = Path.Combine(RuntimeDir, "xray.exe");
            if (!File.Exists(exePath)) return -1;

            var psi = new ProcessStartInfo(exePath, $"run -c \"{configPath}\"")
            {
                WorkingDirectory = RuntimeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["XRAY_LOCATION_ASSET"] = RuntimeDir;
            process = Process.Start(psi);
            if (process == null) return -1;

            if (!await WaitForSocksReadyAsync(port, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false))
                return -1;

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(
                method == PingMethod.ProxyHead ? HttpMethod.Head : HttpMethod.Get, probeUrl);
            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            sw.Stop();
            // generate_204 отвечает 204 No Content на успехе — это ожидаемый "успешный" код,
            // а не ошибка.
            return response.StatusCode is HttpStatusCode.NoContent || response.IsSuccessStatusCode
                ? (int)sw.ElapsedMilliseconds
                : -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* уже мог сам завершиться */ }
                process.Dispose();
            }
            try { File.Delete(configPath); } catch { /* временный файл, не критично */ }
        }
    }

    private static string BuildConfig(VlessNode node, int socksPort)
    {
        var config = JsonNode.Parse(node.ConnectPayloadJson)!.AsObject();
        config["inbounds"] = new JsonArray(new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = socksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["udp"] = false }
        });
        // Для разового замера задержки логи не нужны — тише некуда, чтобы не плодить файлы
        // на каждый пинг.
        config["log"] = new JsonObject { ["loglevel"] = "none" };
        return config.ToJsonString();
    }

    private static async Task<bool> WaitForSocksReadyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
                return true;
            }
            catch (SocketException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
        return false;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
