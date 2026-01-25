using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

static class Program
{
    static (bool emitJson, string? jsonPath) ParseArgs(string[] args)
    {
        // Usage: --emit-json output\batch.json
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--emit-json", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Missing path after --emit-json");

                    return (true, args[i + 1]);
                }
        }

        return (false, null);
    }

    static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var apiKey =
            config["OpenAI:ApiKey"] ??
            Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("ERROR: OpenAI API key not configured.");
            return;
        }

        var inbox = config["Paths:Inbox"] ?? "inbox";
        var processing = config["Paths:Processing"] ?? "processing";
        var processed = config["Paths:Processed"] ?? "processed";
        var failed = config["Paths:Failed"] ?? "failed";
        var outputDir = config["Paths:Output"] ?? "output";
        var manifestPath = Path.Combine(outputDir, "manifest.jsonl");

        if(!Directory.Exists(inbox))
            Directory.CreateDirectory(inbox);
        if(!Directory.Exists(processing))
            Directory.CreateDirectory(processing);
        if(!Directory.Exists(processed))
            Directory.CreateDirectory(processed);
        if(!Directory.Exists(failed))
            Directory.CreateDirectory(failed);
        if(!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Missing OPENAI_API_KEY environment variable.");
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // Process all supported images
        var files = Directory.EnumerateFiles(inbox)
            .Where(f => IsImage(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (emitJson, jsonPath) = ParseArgs(args);

        BatchOutput? batch = emitJson ? new BatchOutput() : null;

        if (emitJson)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath!) ?? ".");
            Console.WriteLine($"JSON mode enabled. Will write: {jsonPath}");
        }

        foreach (var srcPath in files)
        {
            var fileName = Path.GetFileName(srcPath);
            var workPath = Path.Combine(processing, fileName);

            // Atomic move to processing
            try
            {
                File.Move(srcPath, workPath, overwrite: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skip (could not move to processing): {fileName} :: {ex.Message}");
                continue;
            }

            var id = Guid.NewGuid().ToString("N");
            var outTxtPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(fileName) + ".txt");

            try
            {
                var bytes = await File.ReadAllBytesAsync(workPath);
                var base64 = Convert.ToBase64String(bytes);
                var mime = GetMimeType(workPath);

                var result = await ExtractEnvelopeTextAsync(http, base64, mime);

                if (emitJson)
                {
                    var pf = new ProcessedFile
                    {
                        Name = fileName,
                        Sections = result.Blocks
                            .Select(b => new OutputSection
                            {
                                // Keeping each block together as one section
                                Content = (b.Text ?? "").Trim()
                            })
                            .ToList()
                    };

                    batch!.Files.Add(pf);
                }

                // Basic validation heuristic: require some non-trivial text
                var allText = string.Join("\n\n", result.Blocks.Select(b => $"[{b.Label}]\n{b.Text}".Trim()));
                if (string.IsNullOrWhiteSpace(allText) || allText.Count(char.IsLetterOrDigit) < 15)
                    throw new Exception("OCR result too short / empty.");

                // Write output text file (blocks kept together)
                var sb = new StringBuilder();
                sb.AppendLine($"Source: {fileName}");
                sb.AppendLine($"ProcessedUtc: {DateTime.UtcNow:O}");
                sb.AppendLine(new string('-', 40));
                foreach (var b in result.Blocks)
                {
                    sb.AppendLine($"[{b.Label}]");
                    sb.AppendLine((b.Text ?? "").Trim());
                    sb.AppendLine();
                }

                if (result.Notes?.Count > 0)
                {
                    sb.AppendLine("[notes]");
                    foreach (var n in result.Notes) sb.AppendLine($"- {n}");
                }

                await File.WriteAllTextAsync(outTxtPath, sb.ToString(), Encoding.UTF8);

                // Append manifest line (jsonl)
                var manifestLine = JsonSerializer.Serialize(new
                {
                    id,
                    fileName,
                    workPath,
                    outTxtPath,
                    status = "processed",
                    processedUtc = DateTime.UtcNow
                });
                await File.AppendAllTextAsync(manifestPath, manifestLine + "\n");

                // Move image to processed
                var donePath = Path.Combine(processed, fileName);
                File.Move(workPath, donePath, overwrite: true);

                Console.WriteLine($"OK: {fileName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: {fileName} :: {ex.Message}");

                var manifestLine = JsonSerializer.Serialize(new
                {
                    id,
                    fileName,
                    workPath,
                    status = "failed",
                    error = ex.Message,
                    processedUtc = DateTime.UtcNow
                });
                await File.AppendAllTextAsync(manifestPath, manifestLine + "\n");

                // Move to failed (keep for manual review / re-run)
                var failPath = Path.Combine(failed, fileName);
                try { File.Move(workPath, failPath, overwrite: true); } catch { /* ignore */ }
            }

            if (emitJson && batch != null)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(batch, options);
                await File.WriteAllTextAsync(jsonPath!, json, Encoding.UTF8);

                Console.WriteLine($"Wrote JSON batch file: {jsonPath}");
            }
        }
    }

    static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tif" or ".tiff";
    }

    static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    // Model response DTO
    public sealed class EnvelopeResult
    {
        [JsonPropertyName("blocks")]
        public List<TextBlock> Blocks { get; set; } = new();

        [JsonPropertyName("notes")]
        public List<string>? Notes { get; set; }
    }

    public sealed class TextBlock
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    static async Task<EnvelopeResult> ExtractEnvelopeTextAsync(HttpClient http, string base64, string mime)
    {
        // Prompt designed to keep regions together and avoid "helpful" extra text.
        var system = "You are an OCR engine. You only output valid JSON, no markdown, no extra commentary.";
        var user = """
Extract all readable text from this photo of a mailed envelope (handwritten likely).
Group text into blocks so the return address stays together and the recipient address stays together.
Return ONLY JSON in this exact shape:
{
  "blocks": [
    {"label":"return_address","text":"..."},
    {"label":"recipient_address","text":"..."},
    {"label":"other","text":"..."}
  ],
  "notes": ["...optional warnings..."]
}

Rules:
- Preserve line breaks inside each block.
- Do not invent text. If unclear, leave it out and add a note like "illegible section".
- Keep stamps/postmarks/tracking numbers in "other" if readable.
""";

        // Responses API payload (vision input with base64)
        var payload = new
        {
            model = "gpt-4.1-mini", // choose a vision-capable model available to you; adjust as needed
            temperature = 0,
            input = new object[]
            {
                new {
                    role = "system",
                    content = new object[] { new { type = "input_text", text = system } }
                },
                new {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = user },
                        new {
                            type = "input_image",
                            image_url = $"data:{mime};base64,{base64}"
                        }
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        // Simple retry (you can expand to exponential backoff)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var resp = await http.SendAsync(req.Clone());
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                if (attempt == 3) throw new Exception($"API error: {resp.StatusCode} :: {body}");
                await Task.Delay(500 * attempt);
                continue;
            }

            // Responses API returns output; easiest is to extract the first "output_text"
            // We'll parse loosely to avoid depending on a rigid schema.
            var jsonDoc = JsonDocument.Parse(body);
            var text = ExtractOutputText(jsonDoc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("No output_text returned from model.");

            // Now parse the OCR JSON the model returned
            var result = JsonSerializer.Deserialize<EnvelopeResult>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result == null || result.Blocks.Count == 0)
                throw new Exception("Model returned invalid/empty JSON.");

            return result;
        }

        throw new Exception("Unreachable.");
    }

    static string ExtractOutputText(JsonElement root)
    {
        // Typical path: root.output[...].content[...].text
        // We'll search for any field named "output_text" first, then fall back to scanning.
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString() ?? "";

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var type) && type.GetString() == "output_text"
                            && c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            return t.GetString() ?? "";
                        }
                    }
                }
            }
        }
        return "";
    }
}

// Helper to clone HttpRequestMessage for retries (since HttpContent is single-use)
static class HttpRequestMessageExtensions
{
    public static HttpRequestMessage Clone(this HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);

        // headers
        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        // content
        if (req.Content != null)
        {
            var ms = new MemoryStream();
            req.Content.CopyToAsync(ms).GetAwaiter().GetResult();
            ms.Position = 0;
            clone.Content = new StreamContent(ms);

            foreach (var h in req.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        return clone;
    }
}

public sealed class BatchOutput
{
    public List<ProcessedFile> Files { get; set; } = new();
}

public sealed class ProcessedFile
{
    public string Name { get; set; } = "";                 // image filename
    public List<OutputSection> Sections { get; set; } = new();
}

public sealed class OutputSection
{
    public string Content { get; set; } = "";              // one section's text
}
