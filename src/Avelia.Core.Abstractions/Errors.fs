namespace Avelia.Core.Abstractions

/// Expected, recoverable failure that crosses the core/shell boundary. Distinct
/// from a programmer-error exception — the shell renders these as UI (toast,
/// InfoBar, dialog) rather than crashing.
[<RequireQualifiedAccess>]
type AveliaError =
    | NotFound of resource: string
    | Validation of message: string
    | Unauthorized
    | Conflict of message: string
    | Network of message: string
    | Internal of message: string

    /// Visitor over the union — gives C# consumers exhaustive dispatch
    /// (adding a new error case becomes a compile error at every Match site).
    /// Mirrors <c>MessageEvent.Match</c> / <c>DiffKind.Match</c>.
    member this.Match<'TResult>
        (
            onNotFound: System.Func<string, 'TResult>,
            onValidation: System.Func<string, 'TResult>,
            onUnauthorized: System.Func<'TResult>,
            onConflict: System.Func<string, 'TResult>,
            onNetwork: System.Func<string, 'TResult>,
            onInternal: System.Func<string, 'TResult>
        ) : 'TResult =
        match this with
        | NotFound resource -> onNotFound.Invoke resource
        | Validation msg -> onValidation.Invoke msg
        | Unauthorized -> onUnauthorized.Invoke()
        | Conflict msg -> onConflict.Invoke msg
        | Network msg -> onNetwork.Invoke msg
        | Internal msg -> onInternal.Invoke msg

/// C#-friendly wrapper around <c>Result&lt;'T, AveliaError&gt;</c>.
///
/// F# core code may use <see cref="FSharpResult"/> freely, but the shell binds
/// to the boundary in C# where <c>Result</c>-typed values are awkward. The
/// <c>OperationResult</c> shape gives C# <c>IsSuccess</c> / <c>Value</c> /
/// <c>Error</c> properties and a <c>Match</c> method while remaining
/// pattern-matchable in F#.
type OperationResult<'T> =
    | Success of value: 'T
    | Failure of err: AveliaError

    // F# auto-generates IsSuccess / IsFailure from the case names, so we
    // don't declare them here — they're free and the C# binding sees them
    // as boolean properties.

    /// Successful value. Throws <see cref="System.InvalidOperationException"/> if this is a failure.
    member this.Value =
        match this with
        | Success v -> v
        | Failure _ -> invalidOp "OperationResult is Failure; access Error instead."

    /// Failure detail. Throws <see cref="System.InvalidOperationException"/> if this is a success.
    member this.Error =
        match this with
        | Success _ -> invalidOp "OperationResult is Success; access Value instead."
        | Failure e -> e

    /// Pattern-match from C#: returns the result of <paramref name="onSuccess"/> or
    /// <paramref name="onError"/>.
    member this.Match
        (
            onSuccess: System.Func<'T, 'TResult>,
            onError: System.Func<AveliaError, 'TResult>
        ) : 'TResult =
        match this with
        | Success v -> onSuccess.Invoke v
        | Failure e -> onError.Invoke e

/// Helpers for constructing and lifting <see cref="OperationResult&#96;1"/>.
[<RequireQualifiedAccess>]
module OperationResult =
    let ok (value: 'T) : OperationResult<'T> = Success value
    let fail (err: AveliaError) : OperationResult<'T> = Failure err

    let ofResult (r: Result<'T, AveliaError>) : OperationResult<'T> =
        match r with
        | Ok v -> Success v
        | Error e -> Failure e

    let toResult (op: OperationResult<'T>) : Result<'T, AveliaError> =
        match op with
        | Success v -> Ok v
        | Failure e -> Error e

    let map (f: 'T -> 'U) (op: OperationResult<'T>) : OperationResult<'U> =
        match op with
        | Success v -> Success (f v)
        | Failure e -> Failure e

    let bind (f: 'T -> OperationResult<'U>) (op: OperationResult<'T>) : OperationResult<'U> =
        match op with
        | Success v -> f v
        | Failure e -> Failure e
