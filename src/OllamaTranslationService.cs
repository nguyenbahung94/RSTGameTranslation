using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace RSTGameTranslation
{
    public class OllamaTranslationService : ITranslationService
    {
        // Shared across instances (a new service is created per translation) so connections are reused.
        // Local models can be slow, hence the long timeout.
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        internal static OllamaModelDownloader ModelDownloader { get; } = new OllamaModelDownloader();
        private static readonly System.Collections.Generic.Dictionary<string, Task<bool>> _autoDownloads = new();

        private static bool IsModelNotFound(HttpResponseMessage response, string body)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("error", out JsonElement e) ||
                    e.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                string error = e.GetString() ?? "";
                return error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                       error.Contains("not found", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool StartDownloadIfModelMissing(HttpResponseMessage response, string body, string model)
        {
            if (!IsModelNotFound(response, body)) return false;

            var app = System.Windows.Application.Current;
            if (app == null) return false;

            lock (_autoDownloads)
            {
                if (_autoDownloads.TryGetValue(model, out Task<bool>? existing))
                {
                    return !existing.IsCompleted;
                }
                _autoDownloads[model] = StartDownload(app, model);
            }
            return true;
        }

        private static Task<bool> StartDownload(System.Windows.Application app, string model)
        {
            Console.WriteLine($"Ollama model '{model}' is not installed. Downloading it...");
            return app.Dispatcher.InvokeAsync(async () =>
            {
                bool? ready = await ModelDownloader.EnsureModelAsync(model);
                if (ready == true)
                {
                    Logic.Instance.RetryCurrentTranslation();
                }
                else if (ready == null)
                {
                    lock (_autoDownloads)
                    {
                        _autoDownloads.Remove(model);
                    }
                }
                return ready == true;
            }).Task.Unwrap();
        }

        /// <summary>
        /// Load the configured model into memory before the first translation. A request with a
        /// model and no prompt only loads it; a cold model load takes seconds for larger models.
        /// </summary>
        internal static async Task PreloadModelAsync()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string endpoint = ConfigManager.Instance.GetOllamaApiEndpoint();
            string model = ConfigManager.Instance.GetOllamaModel();
            string json = System.Text.Json.JsonSerializer.Serialize(new { model, keep_alive = "30m" });
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content);
            StartDownloadIfModelMissing(response, await response.Content.ReadAsStringAsync(), model);
            Console.WriteLine($"[WarmUp] Ollama: model '{model}' loaded in {timer.ElapsedMilliseconds} ms ({(int)response.StatusCode})");
        }

        /// <summary>
        /// Translate text using the Ollama API
        /// </summary>
        /// <param name="jsonData">The JSON data to translate</param>
        /// <param name="prompt">The prompt to guide the translation</param>
        /// <returns>The translation result formatted to match Gemini's response structure</returns>
        public async Task<string?> TranslateAsync(string jsonData, string prompt)
        {
            try
            {
                // Get the Ollama API endpoint and model from config
                string ollamaEndpoint = ConfigManager.Instance.GetOllamaApiEndpoint();
                string ollamaModel = ConfigManager.Instance.GetOllamaModel();
                
                // Create Ollama API request
                var requestContent = new
                {
                    model = ollamaModel, // Use model from config
                    prompt = $"{prompt}\n{jsonData}",
                    stream = false,
                    keep_alive = "30m", // Keep the model loaded between translations (default unloads after 5 min idle)
                    options = new
                    {
                        temperature = ConfigManager.Instance.GetOllamaTemperature(),
                        top_p = ConfigManager.Instance.GetOllamaTopP(),
                        top_k = ConfigManager.Instance.GetOllamaTopK()
                    },
                    format = "json" // Request JSON formatted response
                };
                
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string requestJson = JsonSerializer.Serialize(requestContent, jsonOptions);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                Console.WriteLine($"Sending request to Ollama API at: {ollamaEndpoint}");
                
                HttpResponseMessage response = await _httpClient.PostAsync(ollamaEndpoint, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    TranslationErrorNotifier.Reset("Ollama");
                    
                    // Log the raw Ollama response before any processing
                    
                    // Parse the Ollama response which has a different format from Gemini
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(jsonResponse);
                        string responseText = doc.RootElement.GetProperty("response").GetString() ?? "";
                        LogManager.Instance.LogLlmReply(responseText); //we could log this earlier, but it
                        //has all this "context" # crap that Ollama returns that we don't want to log

                        // Check if the response contains nested JSON
                        try
                        {
                            // Trim possible markdown code blocks or extra text
                            responseText = responseText.Trim();
                            if (responseText.StartsWith("```json"))
                            {
                                int endIndex = responseText.IndexOf("```", 7);
                                if (endIndex > 0)
                                {
                                    responseText = responseText.Substring(7, endIndex - 7).Trim();
                                }
                            }
                            
                            // If the response doesn't start with '{', look for JSON within the text
                            if (!responseText.StartsWith("{"))
                            {
                                int jsonStart = responseText.IndexOf('{');
                                int jsonEnd = responseText.LastIndexOf('}');
                                
                                if (jsonStart >= 0 && jsonEnd > jsonStart)
                                {
                                    responseText = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
                                }
                            }
                            
                            Console.WriteLine($"Attempting to parse nested JSON: {responseText}");
                            
                            using JsonDocument nestedDoc = JsonDocument.Parse(responseText);
                            
                            // Try to find text_blocks array which contains translations
                            if (nestedDoc.RootElement.TryGetProperty("text_blocks", out JsonElement textBlocks) && 
                                textBlocks.ValueKind == JsonValueKind.Array)
                            {
                                // Don't extract just the text - we want to keep the entire JSON structure
                                // This ensures Logic.cs gets the complete JSON with text_blocks
                                Console.WriteLine("Found structured JSON with text_blocks - keeping entire JSON structure");
                                
                                // Ensure the responseText is the entire JSON object
                                // responseText is already set to the entire JSON
                            }
                            else
                            {
                                // If we don't have the structured JSON we expect, try to extract text
                                // Extract text from the first text block or combine all blocks
                                StringBuilder combinedText = new StringBuilder();
                                
                                // Nested blocks may vary, so try to handle different structures
                                
                                // Try to find any text property at the root level
                                if (nestedDoc.RootElement.TryGetProperty("text", out JsonElement rootTextProp))
                                {
                                    combinedText.AppendLine(rootTextProp.GetString());
                                }
                                
                                if (combinedText.Length > 0)
                                {
                                    responseText = combinedText.ToString().Trim();
                                }
                                else
                                {
                                    // Just use the entire cleaned JSON object as is - Logic.cs can handle it
                                    // Keep the responseText as the cleaned JSON
                                    Console.WriteLine("Using the entire JSON object as responseText");
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            // If nested parsing fails, use the original response text
                            Console.WriteLine($"Could not parse nested JSON, using raw response: {ex.Message}");
                        }
                        
                        // Process the response to match what Logic.cs expects from Gemini
                        var formattedResponse = new
                        {
                            candidates = new[]
                            {
                                new
                                {
                                    content = new
                                    {
                                        parts = new[]
                                        {
                                            new
                                            {
                                                text = responseText
                                            }
                                        }
                                    }
                                }
                            }
                        };
                        
                        return JsonSerializer.Serialize(formattedResponse, jsonOptions);
                    }
                    catch (JsonException jex)
                    {
                        Console.WriteLine($"Error parsing Ollama response: {jex.Message}");
                        Console.WriteLine($"Raw response: {jsonResponse}");
                        return null;
                    }
                }
                else
                {
                    string errorMessage = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Ollama API error: {response.StatusCode}, {errorMessage}");

                    if (StartDownloadIfModelMissing(response, errorMessage, ollamaModel))
                    {
                        return null;
                    }

                    // Try to parse the error message from JSON if possible
                    try
                    {
                        using JsonDocument errorDoc = JsonDocument.Parse(errorMessage);
                        if (errorDoc.RootElement.TryGetProperty("error", out JsonElement errorElement))
                        {
                            string detailedError = errorElement.GetString() ?? errorMessage;
                            
                            // Show error message to user
                            TranslationErrorNotifier.ShowOnce("Ollama",
                                string.Format(LocalizationManager.Instance.Strings["Msg_OllamaError"], detailedError),
                                LocalizationManager.Instance.Strings["Title_OllamaError"]);
                            
                            return null;
                        }
                    }
                    catch (JsonException)
                    {
                        // If we can't parse as JSON, just use the raw message
                    }
                    
                    // Show general error if JSON parsing failed
                    TranslationErrorNotifier.ShowOnce("Ollama",
                        string.Format(LocalizationManager.Instance.Strings["Msg_OllamaApiErrorStatus"], response.StatusCode, errorMessage),
                        LocalizationManager.Instance.Strings["Title_OllamaError"]);
                    
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ollama API error: {ex.Message}");
            
                // Show error message to user for other exceptions
                TranslationErrorNotifier.ShowOnce("Ollama",
                    string.Format(LocalizationManager.Instance.Strings["Msg_OllamaApiException"], ex.Message),
                    LocalizationManager.Instance.Strings["Title_OllamaError"]);
            
                return null;
            }
        }
    }
}