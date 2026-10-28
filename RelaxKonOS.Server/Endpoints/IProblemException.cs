namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// A domain failure that carries the API problem it should become. Endpoints do not translate these
/// one by one: the request pipeline maps any escaping <see cref="IProblemException"/> to the single
/// problem shape (status, <c>title=code</c>, <c>type=problems/{code}</c>, <c>problemCode</c>).
///
/// <para>
/// The type lives in the Server, not in <c>RelaxKonOS.Protocol</c>: it describes how this Host reports
/// a failure, not a wire contract by itself.
/// </para>
/// </summary>
public interface IProblemException
{
    /// <summary>Stable problem code, also used as the localized-API title key.</summary>
    string ProblemCode { get; }

    /// <summary>HTTP status this failure must answer with.</summary>
    int StatusCode { get; }

    /// <summary>Optional human-readable detail. Never contains secrets or raw user data.</summary>
    string? Detail => null;
}
