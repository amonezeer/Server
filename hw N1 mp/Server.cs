using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class CurrencyExchangeServer
{
    private readonly TcpListener _tcpListener;
    private readonly int _maxAttempts;
    private const int RetryTimeInSeconds = 60;
    private readonly Dictionary<string, int> _clientAttempts = new();
    private readonly Dictionary<string, DateTime> _blockedClients = new();
    private Dictionary<string, decimal> _rates = new();
    private const string ApiUrl = "https://api.exchangerate-api.com/v4/latest/USD";
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HashSet<string> _connectedClients = new();

    public CurrencyExchangeServer(string ipAddress, int port, int maxAttempts)
    {
        _tcpListener = new TcpListener(IPAddress.Parse(ipAddress), port);
        _maxAttempts = maxAttempts;
    }

    public async Task StartAsync()
    {
        _tcpListener.Start();
        Console.Clear();
        DisplayHeader();
        Console.WriteLine($"\n[Сервер] запущен на {IPAddress.Loopback}:12345 | Макс. попыток: {_maxAttempts}\n");
        _ = AutoUpdateExchangeRates(_cancellationTokenSource.Token);

        while (true)
        {
            TcpClient client = await _tcpListener.AcceptTcpClientAsync();
            string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

            if (!_connectedClients.Contains(clientIp))
            {
                PrintLog("Сервер", $"Клиент подключен: {clientIp}");
                _connectedClients.Add(clientIp);
            }

            _ = HandleClient(client, clientIp);
        }
    }
    private async Task HandleClient(TcpClient client, string clientIp)
    {
        NetworkStream networkStream = client.GetStream();
        byte[] buffer = new byte[1024];

        bool clientConnected = false;

        try
        {
            if (!_connectedClients.Contains(clientIp))
            {
                PrintLog("Сервер", $"Клиент {clientIp} подключен.");
                _connectedClients.Add(clientIp);
                clientConnected = true;
            }

            int bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead == 0) return;

            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            PrintLog("Запрос", $"Запрос от {clientIp}: {request}");

            string response = ProcessRequest(request, clientIp);
            await networkStream.WriteAsync(Encoding.UTF8.GetBytes(response));
        }
        catch (Exception ex)
        {
            PrintLog("Ошибка", ex.Message);
        }
        finally
        {
            if (clientConnected && _connectedClients.Contains(clientIp))
            {
                PrintLog("Сервер", $"Клиент {clientIp} отключился.");
                _connectedClients.Remove(clientIp);
            }
            client.Close();
        }
    }

    private async Task ReadClientData(TcpClient client, NetworkStream networkStream, byte[] buffer, string clientIp, CancellationToken cancellationToken)
    {
        while (client.Connected && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                int bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }

                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                PrintLog("Запрос", $"Запрос от {clientIp}: {request}");

                string response = ProcessRequest(request, clientIp);
                await networkStream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
            catch (Exception ex)
            {
                PrintLog("Ошибка", ex.Message);
                break;
            }
        }
    }

    private string ProcessRequest(string request, string clientIp)
    {
        if (_blockedClients.TryGetValue(clientIp, out DateTime blockTime))
        {
            if ((DateTime.Now - blockTime).TotalSeconds < RetryTimeInSeconds)
            {
                _blockedClients[clientIp] = DateTime.Now.AddSeconds(RetryTimeInSeconds);
                PrintLog("Запрос", $"Клиент {clientIp} заблокирован. Блокировка продлена");
                return "Превышен лимит запросов. Повторите позже. (Блокировка продлена)";
            }
            else
            {
                PrintLog("Запрос", $"Клиент {clientIp} разблокирован");
                _blockedClients.Remove(clientIp);
                _clientAttempts[clientIp] = 0;
            }
        }

        if (!_clientAttempts.ContainsKey(clientIp))
            _clientAttempts[clientIp] = 0;

        if (request == "ATTEMPTS")
        {
            int remainingAttempts = _maxAttempts - _clientAttempts[clientIp];
            return $"Осталось {remainingAttempts} попыток перед блокировкой.";
        }

        if (_clientAttempts[clientIp] >= _maxAttempts)
        {
            PrintLog("Запрос", $"Клиент {clientIp} заблокирован за превышение лимита попыток");
            _blockedClients[clientIp] = DateTime.Now;
            return "Превышен лимит запросов. Повторите через 1 минуту.";
        }

        _clientAttempts[clientIp]++;

        string[] parts = request.Split(' ');
        if (parts.Length < 3) return "Ошибка: некорректный запрос.";

        if (parts[0] == "CONVERT" && decimal.TryParse(parts[1], out decimal amount) &&
            _rates.ContainsKey(parts[2]) && _rates.ContainsKey(parts[3]))
        {
            decimal result = amount * (_rates[parts[3]] / _rates[parts[2]]);
            return $"Результат: {result:F2} {parts[3]}";
        }

        if (parts[0] == "RATE" && _rates.ContainsKey(parts[1]) && _rates.ContainsKey(parts[2]))
        {
            decimal rate = _rates[parts[2]] / _rates[parts[1]];
            return $"Курс {parts[1]} → {parts[2]}: {rate:F4}";
        }

        return "Ошибка: неизвестный запрос.";
    }

    private async Task AutoUpdateExchangeRates(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await FetchExchangeRates();
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }
    }

    private async Task FetchExchangeRates()
    {
        try
        {
            using HttpClient client = new();
            string response = await client.GetStringAsync(ApiUrl);

            if (string.IsNullOrWhiteSpace(response))
            {
                PrintLog("Сервер", "Пустой ответ от API.");
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("rates", out JsonElement ratesElement))
            {
                PrintLog("Сервер", "Не найден ключ 'rates' в JSON.");
                return;
            }

            _rates = JsonSerializer.Deserialize<Dictionary<string, decimal>>(ratesElement.GetRawText()) ?? new();
            PrintLog("Сервер", "Курсы валют обновлены");
        }
        catch (Exception ex)
        {
            PrintLog("Сервер", ex.Message);
        }
    }

    private void DisplayHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════╗");
        Console.WriteLine("║          Сервер Конвертации Валют          ║ ");
        Console.WriteLine("╚════════════════════════════════════════════╝");
        Console.ResetColor();
    }

    private void PrintLog(string category, string message)
    {
        string currentTime = DateTime.Now.ToString("HH:mm:ss");
        if (category == "Запрос")
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[{category}] | {currentTime} | {message}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{category}] | {currentTime} | {message}");
        }
    }
}

class Program
{
    static async Task Main()
    {
        Console.Write("Введите максимальное число попыток: ");
        if (!int.TryParse(Console.ReadLine(), out int maxAttempts) || maxAttempts <= 0)
        {
            Console.WriteLine("Некорректный ввод. Устанавливаю значение по умолчанию: 3 попытки.");
            maxAttempts = 3;
        }

        var server = new CurrencyExchangeServer("127.0.0.1", 12345, maxAttempts);
        await server.StartAsync();
    }
}
