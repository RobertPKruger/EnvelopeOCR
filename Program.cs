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

        Console.WriteLine($"Found {files.Count} image(s) in inbox.");

        foreach (var srcPath in files)
        {
            var fileName = Path.GetFileName(srcPath);
            var workPath = Path.Combine(processing, fileName);
            Console.WriteLine($"\n--- Processing: {fileName} ---");

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
                Console.WriteLine($"  File size: {bytes.Length:N0} bytes, MIME: {mime}");

                Console.WriteLine("  Calling OpenAI API...");
                var result = await ExtractTextAsync(http, base64, mime);

                if (emitJson)
                {
                    var pf = new ProcessedFile
                    {
                        Name = fileName,
                        Sections = new List<OutputSection>
                        {
                            new OutputSection { Content = (result.Text ?? "").Trim() }
                        }
                    };

                    batch!.Files.Add(pf);
                }

                // Basic validation heuristic: require some non-trivial text
                var extractedText = (result.Text ?? "").Trim();
                var alphanumCount = extractedText.Count(char.IsLetterOrDigit);
                Console.WriteLine($"  Validation: {alphanumCount} alphanumeric chars (minimum: 15)");
                if (string.IsNullOrWhiteSpace(extractedText) || alphanumCount < 15)
                    throw new Exception($"OCR result too short / empty. Only {alphanumCount} alphanumeric chars found.");

                // Write output text file
                var sb = new StringBuilder();
                sb.AppendLine($"Source: {fileName}");
                sb.AppendLine($"ProcessedUtc: {DateTime.UtcNow:O}");
                sb.AppendLine(new string('-', 40));
                sb.AppendLine(extractedText);

                if (result.Notes?.Count > 0)
                {
                    sb.AppendLine();
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
                Console.Error.WriteLine($"FAIL: {fileName}");
                Console.Error.WriteLine($"  Error: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");

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

    static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

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
    public sealed class OcrResult
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("notes")]
        public List<string>? Notes { get; set; }
    }

    static async Task<OcrResult> ExtractTextAsync(HttpClient http, string base64, string mime)
    {
        // Prompt designed for astronomy-related document OCR.
        var system = """
You are an OCR engine specialized in reading astronomy-related documents and photographic plates.
You only output valid JSON, no markdown, no extra commentary.
""";
        var user = """
Extract ALL readable text from this image. The content is astronomy-related.

Context to help with recognition:
- Look for terms like: Plate, Plates, Exposure, asteroid, object, planet, star, telescope, magnitude, epoch, coordinates, RA, Dec, hours, minutes, seconds
- Unfamiliar words may be constellation names (e.g., Serpens, Ophiuchus, Cygnus, Aquila)
- Star designations often follow patterns like "V Serpentis", "V Serp", "RR Lyrae", "SS Cygni" where a letter or letters precede the constellation name
- Variable star types use abbreviations: "V" for variable, "RR", "SS", "UV", etc.
- Numbers may represent dates, plate numbers, exposure times, or celestial coordinates

Return ONLY JSON in this exact shape:
{
  "text": "all extracted text, preserving line breaks",
  "notes": ["...optional warnings about illegible sections..."]
}

Rules:
- Preserve line breaks and spatial groupings where meaningful.
- Do not invent text. If a section is unclear, note it in "notes" and omit that text.
- Include all visible text: handwritten, typed, stamped, or printed.
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
            Console.WriteLine($"  API attempt {attempt}/3...");
            using var resp = await http.SendAsync(req.Clone());
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"  Response status: {(int)resp.StatusCode} {resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Response body: {Truncate(body, 500)}");
                if (attempt == 3) throw new Exception($"API error: {resp.StatusCode} :: {body}");
                Console.WriteLine($"  Retrying in {500 * attempt}ms...");
                await Task.Delay(500 * attempt);
                continue;
            }

            // Responses API returns output; easiest is to extract the first "output_text"
            // We'll parse loosely to avoid depending on a rigid schema.
            var jsonDoc = JsonDocument.Parse(body);
            var text = ExtractOutputText(jsonDoc.RootElement);
            Console.WriteLine($"  Extracted model output ({text.Length} chars): {Truncate(text, 300)}");
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("No output_text returned from model.");

            // Now parse the OCR JSON the model returned
            var result = JsonSerializer.Deserialize<OcrResult>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result == null || string.IsNullOrWhiteSpace(result.Text))
                throw new Exception("Model returned invalid/empty JSON.");

            Console.WriteLine($"  Parsed OCR result: {result.Text.Length} chars");
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
