# Architecture Documentation

## Overview

This project follows SOLID principles and clean code practices, implementing a layered architecture with clear separation of concerns.

## SOLID Principles Applied

### 1. Single Responsibility Principle (SRP)
Each class has one, and only one, reason to change:

- **ConfigurationService**: Handles configuration loading and validation
- **CardParserService**: Parses card names from different formats
- **FileOperationsService**: Manages file system operations
- **ReportService**: Generates and displays reports
- **CardProcessorService**: Orchestrates the workflow

### 2. Open/Closed Principle (OCP)
Classes are open for extension but closed for modification:

- All services implement interfaces, allowing new implementations without modifying existing code
- Card parsing logic can be extended by implementing `ICardParserService`
- New report formats can be added by implementing `IReportService`

### 3. Liskov Substitution Principle (LSP)
Implementations can be substituted without breaking functionality:

- Any implementation of `IConfigurationService` can replace `ConfigurationService`
- Mock implementations can be used for testing
- Alternative file operations providers can be swapped in

### 4. Interface Segregation Principle (ISP)
Interfaces are small and focused:

- `ICardParserService`: Only card parsing methods
- `IFileOperationsService`: Only file operations methods
- `IReportService`: Only reporting methods
- No class is forced to depend on methods it doesn't use

### 5. Dependency Inversion Principle (DIP)
High-level modules depend on abstractions:

- `CardProcessorService` depends on interfaces, not concrete implementations
- Dependencies are injected through constructor
- Easy to swap implementations for testing or extending functionality

## Project Structure

```
CardChooser/
├── Models/                          # Data models
│   ├── AppConfiguration.cs         # Configuration data
│   └── CardInfo.cs                 # Card information
├── Services/                        # Service implementations
│   ├── Interfaces/                 # Service contracts
│   │   ├── ICardParserService.cs
│   │   ├── ICardProcessorService.cs
│   │   ├── IConfigurationService.cs
│   │   ├── IFileOperationsService.cs
│   │   └── IReportService.cs
│   ├── CardParserService.cs        # Card parsing logic
│   ├── CardProcessorService.cs     # Main orchestrator
│   ├── ConfigurationService.cs     # Configuration handling
│   ├── FileOperationsService.cs    # File operations
│   └── ReportService.cs            # Report generation
└── Program.cs                       # Application entry point
```

## Clean Code Practices

### 1. Meaningful Names
- Classes, methods, and variables have descriptive names
- Example: `ExtractCardName()` clearly describes its purpose
- No abbreviations or cryptic names

### 2. Small, Focused Methods
- Each method does one thing well
- Methods are short and easy to understand
- Example: `ValidateConfiguration()` only validates

### 3. Comments and Documentation
- XML documentation for public APIs
- Comments explain "why", not "what"
- Self-documenting code through clear naming

### 4. Error Handling
- Specific exception types
- Meaningful error messages
- Graceful degradation

### 5. DRY (Don't Repeat Yourself)
- Common logic extracted into reusable methods
- `GetUniqueFilePath()` handles duplicate file names
- Card parsing logic centralized

### 6. Separation of Concerns
- UI logic separated from business logic
- File operations isolated from parsing logic
- Configuration handling independent

## Design Patterns Used

### 1. Dependency Injection
Manual dependency injection in `Program.cs`:
```csharp
IConfigurationService configService = new ConfigurationService();
ICardProcessorService processor = new CardProcessorService(
    configService, 
    cardParser, 
    fileOps, 
    reporter
);
```

### 2. Service Layer Pattern
Business logic encapsulated in service classes with clear interfaces.

### 3. Facade Pattern
`CardProcessorService` provides a simple interface to the complex subsystem.

## Benefits of This Architecture

### 1. Testability
- Each service can be unit tested independently
- Mock implementations can be injected for testing
- No hidden dependencies

### 2. Maintainability
- Changes are localized to specific services
- Clear separation makes code easy to understand
- Adding features doesn't break existing code

### 3. Extensibility
- New card formats: Implement `ICardParserService`
- New storage: Implement `IFileOperationsService`
- New reports: Implement `IReportService`

### 4. Reusability
- Services can be reused in other contexts
- Interfaces define clear contracts
- No coupling between services

## How to Extend

### Adding a New Card Format Parser

```csharp
public class AdvancedCardParser : ICardParserService
{
    public string ExtractCardName(string line)
    {
        // Custom parsing logic
    }
    
    public List<string> ReadCardNames(string filePath)
    {
        // Custom reading logic
    }
}
```

### Adding Cloud Storage Support

```csharp
public class CloudFileOperations : IFileOperationsService
{
    public List<string> SearchForCard(string cardName, string sourceFolder)
    {
        // Search in cloud storage
    }
    
    // Implement other interface methods
}
```

### Using the New Services

```csharp
// In Program.cs
ICardParserService parser = new AdvancedCardParser();
IFileOperationsService fileOps = new CloudFileOperations();
// Wire up dependencies
```

## Testing Strategy

### Unit Tests
Test each service independently:
```csharp
[Test]
public void ExtractCardName_WithExtendedFormat_ReturnsCardName()
{
    var parser = new CardParserService();
    var result = parser.ExtractCardName("1 Black Lotus (LEA) 232");
    Assert.AreEqual("Black Lotus", result);
}
```

### Integration Tests
Test service interactions:
```csharp
[Test]
public void ProcessCards_WithValidConfig_CopiesFiles()
{
    var mockConfig = new Mock<IConfigurationService>();
    var processor = new CardProcessorService(mockConfig.Object, ...);
    // Test workflow
}
```

## Performance Considerations

### File Caching
`FileOperationsService` caches directory listings to avoid repeated scans:
```csharp
private List<string>? _cachedFiles;
private string? _cachedDirectory;
```

### Lazy Loading
Services are instantiated only when needed.

### Efficient Searching
Case-insensitive file matching uses LINQ for optimal performance.

## Future Improvements

1. **Dependency Injection Container**: Use Microsoft.Extensions.DependencyInjection
2. **Async/Await**: Make I/O operations asynchronous
3. **Logging**: Add structured logging with ILogger
4. **Configuration**: Use IOptions pattern for configuration
5. **Validation**: Add FluentValidation for input validation
6. **Database**: Add repository pattern for data persistence

## Conclusion

This architecture provides a solid foundation for a maintainable, testable, and extensible application. The separation of concerns and adherence to SOLID principles make the codebase easy to understand and modify.
