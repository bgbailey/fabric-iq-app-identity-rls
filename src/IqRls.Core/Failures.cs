namespace IqRls.Core;

public enum FailureCode
{
    InvalidConfiguration, UnknownSubject, InvalidContext, InvalidToolInput,
    CertificateUnavailable, AuthenticationFailed, TransportFailed, HttpFailure,
    InvalidArrow, QueryError, UnsupportedType, LimitExceeded, UnexpectedResultSets,
    VerificationFailed, InvalidArguments, Cancelled
}

// Only controlled codes cross the broker boundary: no tokens, DAX, raw server faults or SDK inner exceptions.
public sealed class HarnessException(FailureCode code) : Exception(code.ToString())
{
    public FailureCode Code { get; } = code;
}

public static class HarnessContract
{
    public const string Role = "ExternalAppScope";
    public const string ModelAlias = "synthetic-rls-v1";
    public const string EntitlementVersion = "synthetic-v1";
    public const string ResourceScope = "https://analysis.windows.net/powerbi/api/.default";
    public const int MaxRows = 1000;
    public const int MaxColumns = 64;
    public const int MaxDaxCharacters = 32768;
    public const int MaxCellCharacters = 16384;
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    public const int MaxArrowAllocationBytes = 32 * 1024 * 1024;
    public const int MaxOutputCharacters = 2 * 1024 * 1024;
}
