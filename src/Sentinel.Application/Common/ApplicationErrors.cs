using Sentinel.Domain.Common;

namespace Sentinel.Application.Common;

public static class ApplicationErrors
{
    public static readonly Error AdminRequired = Error.Forbidden("Auth.AdminRequired", "This operation requires the Sentinel.Admin role.");
}
