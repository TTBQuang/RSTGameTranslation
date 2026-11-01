using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RSTGameTranslation
{
    public class QwenTranslationService : ITranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>Open the HTTPS connection before the first translation (see TranslationWarmUp).</summary>
        internal static Task WarmUpConnectionAsync() =>
            TranslationWarmUp.OpenConnectionsAsync(_httpClient, "Qwen", "https://dashscope-intl.aliyuncs.com/");
        private static int _consecutiveFailures = 0;
        private int delayMS = 100;
        
        /// <summary>
        /// Translate text using the Qwen API
        /// </summary>
        /// <param name="jsonData">The JSON data to translate</param>
        /// <param name="prompt">The prompt to guide the translation</param>
        /// <returns>The translation result as a JSON string or null if translation failed</returns>
        public Task<string?> TranslateAsync(string jsonData, string prompt) =>
            TranslateAsync(jsonData, prompt, ConfigManager.Instance.GetQwenApiKey(), new HashSet<string>());

        // triedKeys: keys this request already failed with, so a key switch never retries one
        private async Task<string?> TranslateAsync(string jsonData, string prompt, string apiKey, HashSet<string> triedKeys)
        {
            try
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("Qwen API key not configured");
                    return null;
                }
                // Get model from config
                string model = ConfigManager.Instance.GetQwenModel();

                var messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = $"{prompt}\n{jsonData}"
                    }
                };

                // Qwen3.x models think by default, which is far too slow for realtime translation
                object requestContent = model.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase)
                    ? new { model, messages, enable_thinking = false }
                    : new { model, messages };

                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string requestJson = JsonSerializer.Serialize(requestContent, jsonOptions);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                string url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions";
                // The key goes on this request only: the HttpClient is shared by concurrent requests
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                HttpResponseMessage response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    // Reset consecutive failures counter on success
                    _consecutiveFailures = 0;
                    TranslationErrorNotifier.Reset("Qwen");

                    // Log the raw Qwen response before returning it
                    LogManager.Instance.LogLlmReply(jsonResponse);

                    return jsonResponse;
                }
                else
                {
                    string errorMessage = await response.Content.ReadAsStringAsync();
                    _consecutiveFailures++;
                    Console.WriteLine($"Qwen API error: {response.StatusCode}, {errorMessage}, error count: {_consecutiveFailures}");

                    // Rate limit, quota or invalid key: retry with another key
                    if (ApiKeyRotation.ShouldSwitchApiKey(response.StatusCode, errorMessage) &&
                        ApiKeyRotation.GetKeyToRetry("Qwen", apiKey, triedKeys) is string nextKey)
                    {
                        return await TranslateAsync(jsonData, prompt, nextKey, triedKeys);
                    }
                    try
                    {
                        using JsonDocument errorDoc = JsonDocument.Parse(errorMessage);
                        if (errorDoc.RootElement.TryGetProperty("error", out JsonElement errorElement))
                        {
                            string detailedError = "";
                            
                            // Extract error message
                            if (errorElement.TryGetProperty("message", out JsonElement messageElement))
                            {
                                detailedError = messageElement.GetString() ?? "";
                            }
                            // ignore error if it's a rate limit error
                            if (_consecutiveFailures > 3)
                            {
                                // Write error to file
                                System.IO.File.WriteAllText("qwen_last_error.txt", $"Qwen API error: {detailedError}\n\nResponse code: {response.StatusCode}\nFull response: {errorMessage}");
                                
                                // Show error message to user
                                TranslationErrorNotifier.ShowOnce("Qwen",
                                    string.Format(LocalizationManager.Instance.Strings["Msg_QwenApiError"], detailedError),
                                    LocalizationManager.Instance.Strings["Title_QwenError"]);
                            }
                            await Task.Delay(delayMS);
                            return null;
                        }
                    }
                    catch (JsonException)
                    {
                        // If we can't parse as JSON, just use the raw message
                    }
                    if (_consecutiveFailures > 3)
                    {
                        // Write error to file
                        System.IO.File.WriteAllText("qwen_last_error.txt", $"Qwen API error: {response.StatusCode}\n\nFull response: {errorMessage}");
                        
                        // Show general error if JSON parsing failed
                        TranslationErrorNotifier.ShowOnce("Qwen",
                            string.Format(LocalizationManager.Instance.Strings["Msg_QwenApiErrorStatus"], response.StatusCode, errorMessage),
                            LocalizationManager.Instance.Strings["Title_QwenError"]);
                    }
                    await Task.Delay(delayMS);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation API error: {ex.Message}");
                
                // Write error to file
                System.IO.File.WriteAllText("qwen_last_error.txt", $"Qwen API error: {ex.Message}\n\nStack trace: {ex.StackTrace}");
                
                // Show error message to user
                TranslationErrorNotifier.ShowOnce("Qwen",
                    string.Format(LocalizationManager.Instance.Strings["Msg_QwenApiException"], ex.Message),
                    LocalizationManager.Instance.Strings["Title_QwenError"]);
                
                return null;
            }
        }
    }
}