namespace PaperTradingBot.Models;

public class OrderValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static OrderValidationResult Valid()
        => new()
        {
            IsValid = true,
            Errors = Array.Empty<string>()
        };

    public static OrderValidationResult Invalid(params string[] errors)
        => new()
        {
            IsValid = false,
            Errors = errors?.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray() ?? Array.Empty<string>()
        };

    public static OrderValidationResult Invalid(IEnumerable<string> errors)
        => new()
        {
            IsValid = false,
            Errors = errors?.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray() ?? Array.Empty<string>()
        };

    public string ToSingleMessage(string separator = " | ")
        => Errors.Count == 0 ? "Unknown validation error" : string.Join(separator, Errors);
}
