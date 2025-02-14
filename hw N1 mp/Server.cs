using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;

class Server
{
    private static readonly int port = 5000;
    private static readonly string logFile = "server_log.txt";
    private static readonly string apiUrl = "https://api.exchangerate-api.com/v4/latest/";

    static async Task Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Сервер] Запущен на порту {port}");

        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            Thread clientThread = new Thread(HandleClient!);
            clientThread.Start(client);
        }
    }

    private static async void HandleClient(object? obj)
    {
        if (obj is not TcpClient client)
            return;

        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        string clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Неизвестный клиент";
        Log($"Подключился клиент {clientEndPoint}");

        try
        {
            while (true)
            {
                string? request = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(request))
                    break;

                Console.WriteLine($"[Запрос] {clientEndPoint}: {request}");

                string response = await ProcessRequest(request);
                Log($"Запрос: {request} -> Ответ: {response}");
                await writer.WriteLineAsync(response);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ошибка] {ex.Message}");
        }
        finally
        {
            client.Close();
            Log($"Клиент {clientEndPoint} отключился");
        }
    }

    private static async Task<string> ProcessRequest(string request)
    {
        string[] parts = request.Split(' ');
        if (parts.Length == 2)
        {
            return await GetExchangeRate(parts[0].ToUpper(), parts[1].ToUpper());
        }
        else if (parts.Length == 3 && decimal.TryParse(parts[0], out decimal amount))
        {
            return await ConvertCurrency(amount, parts[1].ToUpper(), parts[2].ToUpper());
        }
        else
        {
            return "Ошибка: Неправильный формат запроса, используйте 'USD EUR' или '100 USD EUR'";
        }
    }

    private static async Task<string> GetExchangeRate(string baseCurrency, string targetCurrency)
    {
        try
        {
            using HttpClient httpClient = new HttpClient();
            string url = $"{apiUrl}{baseCurrency}";
            string json = await httpClient.GetStringAsync(url);

            var data = JsonSerializer.Deserialize<JsonElement>(json);
            if (!data.TryGetProperty("rates", out JsonElement rates) || !rates.TryGetProperty(targetCurrency, out JsonElement rate))
                return "Ошибка: Валюта не найдена";

            return $"{baseCurrency} -> {targetCurrency}: {rate.GetDecimal()}";
        }
        catch
        {
            return "Ошибка: Не удалось получить курс валют";
        }
    }

    private static async Task<string> ConvertCurrency(decimal amount, string baseCurrency, string targetCurrency)
    {
        try
        {
            using HttpClient httpClient = new HttpClient();
            string url = $"{apiUrl}{baseCurrency}";
            string json = await httpClient.GetStringAsync(url);

            var data = JsonSerializer.Deserialize<JsonElement>(json);
            if (!data.TryGetProperty("rates", out JsonElement rates) || !rates.TryGetProperty(targetCurrency, out JsonElement rate))
                return "Ошибка: Валюта не найдена";

            decimal conversionRate = rate.GetDecimal();
            decimal convertedAmount = amount * conversionRate;

            return $"{amount} {baseCurrency} -> {convertedAmount:F2} {targetCurrency} (курс: {conversionRate})";
        }
        catch
        {
            return "Ошибка: Не удалось конвертировать валюту";
        }
    }

    private static void Log(string message)
    {
        string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(logMessage);
        File.AppendAllText(logFile, logMessage + Environment.NewLine);
    }
}
