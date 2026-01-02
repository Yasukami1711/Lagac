using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;


class Program
{
    static async Task Main()
    {
        await ChatAi.Instance.RunAsync();
    }
}

public class ChatAi
{
    private static ChatAi? _instance;
    public static ChatAi Instance => _instance ??= new ChatAi();

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly HttpClient _client;
    private AppSettings _settings = new();
    private string _systemPrompt = string.Empty;

    private ChatAi()
    {
        _client = new HttpClient();
    }

    private static string Preprompt(string commandShell) => 
        $"あなたは優秀なAIアシスタントです。ステップバイステップで考え、正確かつ詳細に回答してください。\nもし回答を生成するうえで{commandShell}コマンドを提示する場合は、そのコマンドをコピペできるように```{commandShell} ```で囲むようにしてください\n\n";

    public async Task RunAsync()
    {
        var settingsJson = File.Exists(SettingsPath) 
            ? await File.ReadAllTextAsync(SettingsPath) 
            : "{}";
        _settings = JsonSerializer.Deserialize<AppSettings>(settingsJson) ?? new AppSettings();

        if (_settings.MaxFileSize <= 0)
        {
            _settings.MaxFileSize = 1024;
        }

        _systemPrompt = Preprompt(OperatingSystem.IsWindows() ? "powershell" : "bash");

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            Console.WriteLine("保存されたAPI Keyの有効性を確認しています...");
            _client.DefaultRequestHeaders.Clear();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.ApiKey}");
            try
            {
                var response = await _client.GetAsync("https://api.groq.com/openai/v1/models");
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"保存されたAPI Keyが無効です (Status: {response.StatusCode})。");
                    _settings.ApiKey = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接続確認中にエラーが発生しました: {ex.Message}");
                _settings.ApiKey = null;
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            var key = await SetupApiKeyAsync(_settings);
            if (string.IsNullOrWhiteSpace(key)) return;

            _client.DefaultRequestHeaders.Clear();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.ApiKey}");
        }

        await StartChatLoopAsync();
    }

    private async Task StartChatLoopAsync()
    {
        Console.WriteLine("モデル一覧を取得しています...");
        var modelsResponse = await _client.GetAsync("https://api.groq.com/openai/v1/models");
        var modelsJson = await modelsResponse.Content.ReadAsStringAsync();
        var models = JsonSerializer.Deserialize<ModelList>(modelsJson);
        
        var allModels = models?.Data?
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        // 優先順位: Llama 3 -> Mixtral -> その他 (guard/whisper/orpheus 除外)
        var modelId = allModels?.FirstOrDefault(id => id!.Contains("llama-3") && !id.Contains("guard"))
                   ?? allModels?.FirstOrDefault(id => id!.Contains("mixtral"))
                   ?? allModels?.FirstOrDefault(id => !id!.Contains("guard") && !id.Contains("whisper") && !id.Contains("orpheus"));

        modelId ??= allModels?.FirstOrDefault();

        Console.WriteLine($"モデル '{modelId}' でチャットを開始します。(終了するには空行を入力)");

        while (true)
        {
            Console.Write("You: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) break;

            var contextBuilder = new StringBuilder();
            var currentInput = input;

            while (true)
            {
                var span = currentInput.AsSpan().TrimStart();
                if (span.IsEmpty) break;

                if (span.StartsWith(">"))
                {
                    var (command, remainder) = ParseCommand(span.Slice(1).Trim());
                    var output = await ExecuteCommandAsync(command);
                    contextBuilder.AppendLine($"Command: {command}");
                    contextBuilder.AppendLine("```");
                    contextBuilder.AppendLine(output);
                    contextBuilder.AppendLine("```");
                    currentInput = remainder;
                }
                else if (span.StartsWith("@"))
                {
                    var (filePath, remainder) = ParseCommand(span.Slice(1).Trim());
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var fileContent = await File.ReadAllTextAsync(filePath);
                            if (fileContent.Length <= _settings.MaxFileSize)
                            {
                                contextBuilder.AppendLine($"File: {filePath}");
                                contextBuilder.AppendLine("```");
                                contextBuilder.AppendLine(fileContent);
                                contextBuilder.AppendLine("```");
                            }
                            else
                            {
                                Console.WriteLine("ファイル内容が大きすぎます。よって、このファイル内容は無視されます。");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ファイル読み込みエラー: {ex.Message}");
                        }
                    }
                    currentInput = remainder;
                }
                else
                {
                    break;
                }
            }

            if (contextBuilder.Length > 0)
            {
                input = $"{currentInput}\n\n{contextBuilder}";
            }

            var enhancedInput = _systemPrompt + input;

            var requestBody = new
            {
                model = modelId,
                messages = new[]
                {
                    new { role = "user", content = enhancedInput }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(
                "https://api.groq.com/openai/v1/chat/completions",
                content
            );

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"APIエラーが発生しました (Status: {response.StatusCode})");
                Console.WriteLine($"詳細: {jsonResponse}");
                continue;
            }

            var data = JsonSerializer.Deserialize<ChatResponse>(jsonResponse);

            var dataContent = data?.Choices?[0].Message?.Content;
            Console.WriteLine($"AI: {dataContent}");

            if (!string.IsNullOrEmpty(dataContent))
            {
                await ProcessCodeBlocksAsync(dataContent);
            }
        }
    }

    private static async Task<string?> SetupApiKeyAsync(AppSettings settings)
    {
        const string url = "https://console.groq.com/keys";
        Console.WriteLine("API Keyが設定されていません。ブラウザで取得ページを開きます...");
        Console.WriteLine($"もしブラウザが自動で開かれない場合は、以下のURLからAPI Keyを取得してください:\n{url}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = url,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        while (true)
        {
            Console.Write("取得したAPI Keyを入力してください: ");
            var apiKey = Console.ReadLine() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("API Keyが入力されませんでした。終了します。");
                return null;
            }

            Console.WriteLine("API Keyの有効性を確認しています...");
            using var testClient = new HttpClient();
            testClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            try
            {
                var testResponse = await testClient.GetAsync("https://api.groq.com/openai/v1/models");
                if (!testResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"API Keyが無効のようです (Status: {testResponse.StatusCode})。");
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接続確認中にエラーが発生しました: {ex.Message}");
                continue;
            }

            settings.ApiKey = apiKey;
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(settings, options));

            return apiKey;
        }
    }

    private static async Task ProcessCodeBlocksAsync(string content)
    {
        var pattern = OperatingSystem.IsWindows() ? @"```powershell\s+(.*?)\s+```" : @"```bash\s+(.*?)\s+```";
        var regex = new Regex(pattern, RegexOptions.Singleline);
        var matches = regex.Matches(content);

        bool skipsQuestion = false;
        foreach (Match match in matches)
        {
            var command = match.Groups[1].Value.Trim();

            if (skipsQuestion)
            {
                await ExecuteCommandAsync(command);
                continue;
            }

            Console.WriteLine("\n--- 検出されたコマンド ---");
            Console.WriteLine(command);
            Console.WriteLine("--------------------------");
            Console.Write("これを実行しますか？ (y/n): ");

            while (true) {
                var input = Console.ReadLine();
                switch (input?.Trim())
                {
                    case "y":
                        await ExecuteCommandAsync(command);
                        goto WHILE_END;
                    case "n":
                        Console.WriteLine("コマンドをスキップしました。");
                        goto WHILE_END;
                    case "Y":
                        skipsQuestion = true;
                        await ExecuteCommandAsync(command);
                        goto WHILE_END;
                    case "N":
                        Console.WriteLine("コマンドを全てスキップしました。");
                        goto FOREACH_END;
                    default:
                        Console.WriteLine("無効な入力です。コマンドをスキップしました。");
                        break;
                }
            }
            WHILE_END:
            continue;
        }
        FOREACH_END:
        return;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static async Task<string> ExecuteCommandAsync(string command)
    {
        var result = new StringBuilder();
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                // PowerShell経由で実行し、エンコーディングをUTF-8に統一して文字化けを防ぐ
                var escapedCommand = new StringBuilder(command).Replace("\r\n", "; ").Replace("\n", "; ").Replace("\"", "\\\"").ToString();
                psi = CreateProcessStartInfo("powershell.exe", $"-NoProfile -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; {escapedCommand}\"");
            }
            else
            {
                var escapedCommand = command.Replace("\"", "\\\"");
                psi = CreateProcessStartInfo("bash", $"-c \"{escapedCommand}\"");
            }

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(output)) 
                {
                    Console.WriteLine(output);
                    result.AppendLine(output);
                }
                if (!string.IsNullOrWhiteSpace(error)) 
                {
                    Console.WriteLine($"エラー出力: {error}");
                    result.AppendLine($"Error: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"実行時に例外が発生しました: {ex.Message}");
            result.AppendLine($"Exception: {ex.Message}");
        }
        return result.ToString();
    }

    private static (string, string) ParseCommand(ReadOnlySpan<char> input)
    {
        ReadOnlySpan<char> command;
        ReadOnlySpan<char> remainder = ReadOnlySpan<char>.Empty;

        if (input.Length > 0 && input[0] == '"')
        {
            var content = input.Slice(1);
            var endQuoteIndex = content.IndexOf('"');
            if (endQuoteIndex != -1)
            {
                command = content.Slice(0, endQuoteIndex);
                if (endQuoteIndex + 1 < content.Length)
                {
                    remainder = content.Slice(endQuoteIndex + 1).Trim();
                }
            }
            else
            {
                command = content;
            }
        }
        else
        {
            var firstSpaceIndex = input.IndexOf(' ');
            if (firstSpaceIndex != -1)
            {
                command = input.Slice(0, firstSpaceIndex);
                remainder = input.Slice(firstSpaceIndex + 1).Trim();
            }
            else
            {
                command = input;
            }
        }

        return (command.ToString(), remainder.ToString());
    }
}