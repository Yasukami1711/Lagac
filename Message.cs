using System.Text.Json.Serialization;

public class ChatResponse
{
    [JsonPropertyName("choices")]
    public Choice[]? Choices { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")]
    public Message? Message { get; set; }
}

public class Message
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public class ModelList
{
    [JsonPropertyName("data")]
    public ModelInfo[]? Data { get; set; }
}

public class ModelInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public class AppSettings
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("maxFileSize")]
    public int MaxFileSize { get; set; }
}