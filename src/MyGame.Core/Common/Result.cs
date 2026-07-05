namespace MyGame.Core.Common;

/// <summary>
/// Discriminated success/failure type — used in place of thrown exceptions
/// for expected, recoverable failures (rule resolution, save load, network
/// calls). A <see cref="Result{T}"/> is either:
///   - a success: <see cref="Value"/> is set, <see cref="Error"/> is null,
///     <see cref="IsSuccess"/> is true; or
///   - a failure: <see cref="Error"/> is non-null, <see cref="Value"/> is
///     default, <see cref="IsSuccess"/> is false.
///
/// Use <see cref="Result.Ok{T}"/> and <see cref="Result.Fail{T}"/> to
/// construct instances.
/// </summary>
public readonly record struct Result<T>
{
    /// <summary>The value on success; default on failure.</summary>
    public T? Value { get; init; }

    /// <summary>Error message on failure; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>True if this is a success result.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>True if this is a failure result.</summary>
    public bool IsFailure => Error is not null;

    /// <summary>
    /// Construct explicitly. Prefer the <see cref="Result.Ok{T}"/> /
    /// <see cref="Result.Fail{T}"/> factories for readability.
    /// </summary>
    public Result(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Deconstruct for pattern matching.</summary>
    public void Deconstruct(out T? value, out string? error)
    {
        value = Value;
        error = Error;
    }
}

/// <summary>
/// Factory methods for <see cref="Result{T}"/>.
/// </summary>
public static class Result
{
    /// <summary>Build a success result carrying <paramref name="value"/>.</summary>
    public static Result<T> Ok<T>(T value) => new(value, null);

    /// <summary>
    /// Build a failure result carrying <paramref name="error"/>.
    /// <typeparamref name="T"/> must be specified explicitly because there is
    /// no value to infer it from.
    /// </summary>
    public static Result<T> Fail<T>(string error) => new(default, error);
}
