using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class CurrencyExchangeServer
{
    private TcpListener _tcpListener;
    private int _maxAttempts; // Теперь настраивается перед запуском
    private const int RetryTimeInSeconds = 60;
    private Dictionary<string, int> _clientAttempts = new();
    private Dictionary<string, DateTime> _blockedClients = new();
    private Dictionary<string, decimal> _rates = new();
    private const string ApiUrl = "https://api.exchangerate-api.com/v4/latest/USD";

    public CurrencyExchangeServer(int maxAttempts)
    {
        _maxAttempts = maxAttempts;
    }

    public async Task StartAsync(string ipAddress, int port)
    {
        _tcpListener = new TcpListener(IPAddress.Parse(ipAddress), port);
        _tcpListener.Start();
        Console.WriteLine($"Сервер запущен на {ipAddress}:{port} | Макс. попыток: {_maxAttempts}");

        await FetchExchangeRates();

        while (true)
        {
            TcpClient client = await _tcpListener.AcceptTcpClientAsync();
            string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            Console.WriteLine($"Клиент подключен: {clientIp}");

            _ = HandleClient(client, clientIp);
        }
    }

    private async Task HandleClient(TcpClient client, string clientIp)
    {
        NetworkStream networkStream = client.GetStream();
        byte[] buffer = new byte[1024];
        int bytesRead;

        try
        {
            while ((bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (_blockedClients.TryGetValue(clientIp, out DateTime blockTime))
                {
                    if ((DateTime.Now - blockTime).TotalSeconds < RetryTimeInSeconds)
                    {
                        Console.WriteLine($"Клиент {clientIp} всё ещё заблокирован.");
                        string blockMessage = "Превышен лимит запросов. Повторите позже.";
                        await networkStream.WriteAsync(Encoding.UTF8.GetBytes(blockMessage));
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"Клиент {clientIp} разблокирован.");
                        _blockedClients.Remove(clientIp);
                        _clientAttempts[clientIp] = 0;
                    }
                }

                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Запрос от {clientIp}: {request}");

                if (!_clientAttempts.ContainsKey(clientIp))
                    _clientAttempts[clientIp] = 0;

                if (_clientAttempts[clientIp] >= _maxAttempts)
                {
                    Console.WriteLine($"Клиент {clientIp} заблокирован за превышение попыток ({_maxAttempts}).");
                    _blockedClients[clientIp] = DateTime.Now;
                    string blockMessage = "Превышен лимит запросов. Попробуйте через 1 минуту.";
                    await networkStream.WriteAsync(Encoding.UTF8.GetBytes(blockMessage));
                    return;
                }

                _clientAttempts[clientIp]++;
                int remainingAttempts = _maxAttempts - _clientAttempts[clientIp];
                Console.WriteLine($"Клиент {clientIp} использовал {_clientAttempts[clientIp]} попыток. Осталось: {remainingAttempts}");

                string response = ProcessRequest(request);
                await networkStream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
        finally
        {
            client.Close();
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
                Console.WriteLine("Ошибка: пустой ответ от API.");
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("rates", out JsonElement ratesElement))
            {
                Console.WriteLine("Ошибка: не найден ключ 'rates' в JSON.");
                return;
            }

            _rates = JsonSerializer.Deserialize<Dictionary<string, decimal>>(ratesElement.GetRawText()) ?? new();
            Console.WriteLine("Курсы валют обновлены.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки курса: {ex.Message}");
        }
    }

    private string ProcessRequest(string request)
    {
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
}

// Запуск сервера с возможностью указания числа попыток
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

        var server = new CurrencyExchangeServer(maxAttempts);
        await server.StartAsync("127.0.0.1", 12345);
    }
}
