using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

public class CurrencyExchangeServer
{
    private TcpListener _tcpListener;
    private const int MaxAttempts = 3;
    private const int RetryTimeInSeconds = 60;
    private Dictionary<IPEndPoint, int> _clientAttempts = new();
    private Dictionary<IPEndPoint, DateTime> _blockedClients = new();

    private readonly Dictionary<string, decimal> _rates = new()
    {
        { "USD", 1.0m }, { "EUR", 0.85m }, { "UAH", 39.5m },
        { "RUB", 90.0m }, { "PLN", 4.2m }, { "BYN", 3.3m },
        { "GBP", 0.75m }, { "CNY", 7.1m }, { "JPY", 145.0m },
        { "CAD", 1.3m }, { "AUD", 1.5m }, { "CHF", 0.91m }
    };

    public async Task StartAsync(string ipAddress, int port)
    {
        _tcpListener = new TcpListener(IPAddress.Parse(ipAddress), port);
        _tcpListener.Start();
        Console.WriteLine($"Сервер запущен на {ipAddress}:{port}");

        while (true)
        {
            TcpClient client = await _tcpListener.AcceptTcpClientAsync();
            IPEndPoint clientEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
            Console.WriteLine($"Клиент подключен: {clientEndPoint}");

            _ = HandleClient(client);
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        IPEndPoint clientEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
        NetworkStream networkStream = client.GetStream();
        byte[] buffer = new byte[1024];
        int bytesRead;

        try
        {
            while ((bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (_blockedClients.TryGetValue(clientEndPoint, out DateTime blockTime) &&
                    (DateTime.Now - blockTime).TotalSeconds < RetryTimeInSeconds)
                {
                    string blockMessage = "Заблокировано. Повторите через 1 минуту.";
                    await networkStream.WriteAsync(Encoding.UTF8.GetBytes(blockMessage));
                    return;
                }

                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Запрос от {clientEndPoint}: {request}");

                if (!_clientAttempts.ContainsKey(clientEndPoint))
                    _clientAttempts[clientEndPoint] = 0;

                if (_clientAttempts[clientEndPoint] >= MaxAttempts)
                {
                    _blockedClients[clientEndPoint] = DateTime.Now;
                    string blockMessage = "Превышен лимит запросов. Попробуйте через 1 минуту.";
                    await networkStream.WriteAsync(Encoding.UTF8.GetBytes(blockMessage));
                    return;
                }

                _clientAttempts[clientEndPoint]++;
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

// Запуск сервера
class Program
{
    static async Task Main()
    {
        var server = new CurrencyExchangeServer();
        await server.StartAsync("127.0.0.1", 12345);
    }
}
