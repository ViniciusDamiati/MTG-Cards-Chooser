# Coding Patterns & Standards — MTG Cards Chooser

This document captures every design decision and coding convention agreed upon for this project.
All new code **must** follow these rules.

---

## 1. Architecture — Single Responsibility Principle (SRP)

Each class does **one thing only**. The responsibility breakdown is:

| Class | Single Responsibility |
|---|---|
| `ConfigurationService` | Load and validate `config.txt` |
| `CardParserService` | Parse card names from the input file |
| `FileOperationsService` | Scan, search, and copy files on disk |
| `ReportService` | All console output and report file writing |
| `ScryfallService` | All Scryfall HTTP communication |
| `CardProcessorService` | Orchestrate the full workflow |

> **Rule:** If a new feature touches more than one of the above responsibilities, it must be split across the relevant services — not lumped into one class.

---

## 2. Dependency Injection (Manual DI)

All service dependencies are **constructor-injected** via interfaces. No service creates its own dependencies (no `new XxxService()` inside a service).

```csharp
// Program.cs — the only place where `new` is used for services
IConfigurationService configurationService = new ConfigurationService();
ICardProcessorService cardProcessorService = new CardProcessorService(
    configurationService, cardParserService, fileOperationsService,
    reportService, scryfallService);
```

**Every service parameter is validated at construction time:**
```csharp
public CardProcessorService(IConfigurationService configurationService, ...)
{
    _configurationService = configurationService
        ?? throw new ArgumentNullException(nameof(configurationService));
}
```

---

## 3. Interface-First Design

Every service has a matching interface in `Services/Interfaces/`.  
Code always refers to the interface type, never the concrete type.

```csharp
// Always this:
ICardParserService _cardParserService;

// Never this:
CardParserService _cardParserService;
```

All interface members must carry full XML `<summary>`, `<param>`, and `<returns>` documentation.

---

## 4. Naming Conventions

### Variables & Parameters
- **Minimum length: 3 characters.** One- or two-letter names are forbidden, including lambda parameters.
- `_` (C# discard) is the only allowed one-character identifier and only in the context of `out _` or fire-and-forget `_ = Task.Run(...)`.

```csharp
// FORBIDDEN
cards.Where(c => c.Found)
results.Count(r => r)

// REQUIRED
cards.Where(card => card.Found)
results.Count(result => result)
```

### Casing rules
| Element | Convention | Example |
|---|---|---|
| Classes / Interfaces | PascalCase | `CardParserService`, `IReportService` |
| Public methods & properties | PascalCase | `ReadCardNames`, `InputCardsFile` |
| Private fields | `_camelCase` | `_httpClient`, `_cachedFiles` |
| Local variables | `camelCase` | `cardNames`, `matchingFiles` |
| Constants | PascalCase | `ScryfallNamedCardBaseUrl` |
| Method parameters | `camelCase` | `configFilePath`, `targetFolder` |

---

## 5. XML Documentation Comments

**Every** public and private method, class, property, and constructor must have a `<summary>` block.  
Public API members must also include `<param>`, `<returns>`, and `<exception>` where applicable.

```csharp
/// <summary>
/// Reads and parses all card names from the specified file.
/// Blank lines and lines that produce an empty card name are ignored.
/// </summary>
/// <param name="filePath">Absolute or relative path to the card-list file.</param>
/// <returns>A list of extracted card names.</returns>
/// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
public List<string> ReadCardNames(string filePath) { ... }
```

Use `<inheritdoc />` on concrete implementations of interface methods to avoid duplication.

---

## 6. Async / Parallel Pattern — HTTP Rate Limiting

Proxyshop and Scryfall both enforce **≤ 2 HTTP requests per second**.  
The pattern used is a **single-permit `SemaphoreSlim` released after a fixed delay measured from acquisition** (not from completion):

```csharp
private readonly SemaphoreSlim _httpRateLimiter = new SemaphoreSlim(1, 1);
private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMilliseconds(500);

private async Task<HttpResponseMessage> ThrottledGetAsync(string url)
{
    await _httpRateLimiter.WaitAsync();
    ScheduleRateLimiterRelease();          // release 500ms from NOW
    return await _httpClient.GetAsync(url);
}

private void ScheduleRateLimiterRelease()
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(RateLimitWindow);
        _httpRateLimiter.Release();
    });
}
```

**Why this works:** The 500ms window starts when the permit is *acquired*, not when the response arrives. This caps new requests at exactly 2/s regardless of how long each request takes.  
All downloads still run concurrently via `Task.WhenAll`; they just queue at the HTTP level.

---

## 7. Console Output — No Interleaving

When parallel tasks write to the console, **always use a single `Console.WriteLine` call** that includes both the card name and the result. Never split it across `Console.Write` + `Console.WriteLine` on different continuations.

```csharp
// WRONG — name and result can be written by different tasks at different times
Console.Write($"  Downloading '{cardName}'... ");
// ... await ...
Console.WriteLine("OK");

// CORRECT — one atomic call after the result is known
Console.WriteLine($"  '{cardName}'... OK");
Console.WriteLine($"  '{cardName}'... FAILED (card not found or has no image)");
```

---

## 8. File Report Pattern

When a batch operation produces failures, always write a plain-text report file alongside the output so the user has a durable record:

```csharp
var lines = new List<string>
{
    "Failed Downloads",
    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    $"Total failed: {failedCards.Count}",
    string.Empty
};
lines.AddRange(failedCards);
await File.WriteAllLinesAsync(reportPath, lines);
```

The report file lives in the **same folder as the output artefacts** it describes.

---

## 9. Correlating Names with Task Results

When using `Task.WhenAll` you lose the mapping between inputs and outputs.  
Always use `.Zip` immediately after `WhenAll` to correlate them:

```csharp
bool[] results = await Task.WhenAll(downloadTasks);

var failedCards = cardNames
    .Zip(results, (name, succeeded) => (Name: name, Succeeded: succeeded))
    .Where(pair => !pair.Succeeded)
    .Select(pair => pair.Name)
    .ToList();
```

---

## 10. Error Handling

- **LINQ lambdas** — never throw from inside a Select/Where; let the calling method catch.
- **HTTP errors** — catch `HttpRequestException` separately from `Exception` to log the HTTP status code:
  ```csharp
  catch (HttpRequestException ex)
  {
      Console.WriteLine($"  '{cardName}'... FAILED ({ex.StatusCode?.ToString() ?? ex.Message})");
  }
  catch (Exception ex)
  {
      Console.WriteLine($"  '{cardName}'... FAILED ({ex.Message})");
  }
  ```
- **Configuration errors** — catch `FileNotFoundException` at the top-level orchestrator and display a user-friendly help message.

---

## 11. Static Helpers

Pure utility methods with no state must be `private static`:
```csharp
private static string SanitizeFileName(string name) { ... }
private static string BuildCardJsonUrl(string cardName) { ... }
```

---

## 12. Project File Conventions

```
Models/          ← plain data classes (no logic)
Services/        ← service implementations
Services/Interfaces/ ← one interface per service
Program.cs       ← DI wiring + entry point only
```

No business logic goes in `Program.cs`. It wires dependencies and calls `ProcessCardsAsync`.
