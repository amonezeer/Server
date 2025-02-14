using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class CurrencyClient
{
    private static readonly string serverIp = "127.0.0.1";
    private static readonly int serverPort = 5000;

    static async Task Main()
    {
        try
        {
            using TcpClient client = new TcpClient();
            await client.ConnectAsync(serverIp, serverPort);
            NetworkStream stream = client.GetStream();
            StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            Console.WriteLine("Подключено к серверу. Введите пару валют (например, USD EUR) или 'exit' для выхода.");

            while (true)
            {
                Console.Write("Запрос: ");
                string? request = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(request) || request.ToLower() == "exit")
                    break;

                await writer.WriteLineAsync(request);
                string? response = await reader.ReadLineAsync();

                Console.WriteLine($"Ответ: {response ?? "Ошибка получения данных"}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
    }
}
