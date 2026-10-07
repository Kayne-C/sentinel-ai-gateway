namespace Sentinel.Infrastructure.IntegrationTests;

/// <summary>
/// Container-backed tests skip only when there is no Docker at all (a laptop without it). When Docker is there but a
/// container fails to start — an image that cannot be pulled, an out-of-memory exit, a timeout — the tests must fail
/// with the real reason: a silent skip would let CI report green while the coverage is gone.
/// </summary>
internal static class DockerAvailability
{
    private static readonly string[] MissingMarkers =
    [
        "DockerUnavailable",
        "Docker is either not running or misconfigured",
        "Cannot connect to the Docker daemon",
        "docker.sock",
        "DOCKER_HOST",
        "docker_engine",
    ];

    public static bool IsMissing(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var text = current.GetType().Name + " " + current.Message;
            if (MissingMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
