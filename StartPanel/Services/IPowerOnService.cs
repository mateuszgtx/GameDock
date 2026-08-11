namespace WolfControl.Services;

public sealed record PowerOnResult(
    bool Success,
    string Message);

public interface IPowerOnService
{
    Task<PowerOnResult> PowerOnAsync(
        CancellationToken cancellationToken);
}
