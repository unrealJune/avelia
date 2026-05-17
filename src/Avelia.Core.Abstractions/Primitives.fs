namespace Avelia.Core.Abstractions

open System

/// A git branch name. Wraps a string with validation so callers can't mix it
/// up with arbitrary paths or labels. Uses a private constructor; obtain via
/// <c>BranchName.TryCreate</c> (safe) or <c>BranchName.Create</c> (throws on
/// invalid input — use for test fixtures / design data).
[<Struct>]
type BranchName =
    private
    | BranchName of string

    member this.Value = let (BranchName s) = this in s
    override this.ToString() = this.Value

    /// Validate and construct. Returns <c>Ok</c> on success or <c>Error</c> with
    /// a human-readable reason. Validation mirrors git's ref-name rules at the
    /// 80% level — empty/whitespace and obvious metacharacters are rejected;
    /// uncommon edge cases (consecutive dots, trailing slash, etc.) are not
    /// enforced here and would surface as a git error downstream.
    static member TryCreate(s: string) : Result<BranchName, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Branch name cannot be null, empty, or whitespace."
        elif s.StartsWith "/" || s.EndsWith "/" then
            Error "Branch name cannot start or end with '/'."
        elif s.Contains ".." then
            Error "Branch name cannot contain '..'."
        elif s.IndexOfAny([| ' '; '\t'; '\n'; ':'; '?'; '['; '*'; '\\'; '~'; '^' |]) >= 0 then
            Error "Branch name contains an invalid character."
        else
            Ok (BranchName s)

    /// Throwing variant for code paths where the input is known to be valid
    /// (design data, hardcoded fixtures, etc.). Throws <see cref="System.ArgumentException"/>
    /// when validation fails.
    static member Create(s: string) : BranchName =
        match BranchName.TryCreate s with
        | Ok b -> b
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// An absolute path on disk to a repository working tree root.
[<Struct>]
type RepoPath =
    private
    | RepoPath of string

    member this.Value = let (RepoPath p) = this in p
    override this.ToString() = this.Value

    static member private ContainsTraversal(s: string) =
        let parts = s.Replace('\\', '/').Split('/')
        parts |> Array.exists (fun p -> p = "..")

    /// Validate and construct. Rejects empty paths and any path containing a
    /// <c>..</c> segment (defence in depth — the storage layer also validates,
    /// but catching here keeps untrusted UI input from ever creating a domain
    /// value).
    static member TryCreate(s: string) : Result<RepoPath, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Repository path cannot be null, empty, or whitespace."
        elif RepoPath.ContainsTraversal s then
            Error "Repository path cannot contain a '..' segment."
        else
            Ok (RepoPath s)

    static member Create(s: string) : RepoPath =
        match RepoPath.TryCreate s with
        | Ok p -> p
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// A path relative to a repository root. Used in diffs, file lists, and code-refs.
/// Always uses forward slashes regardless of platform.
[<Struct>]
type RelativePath =
    private
    | RelativePath of string

    member this.Value = let (RelativePath p) = this in p
    override this.ToString() = this.Value

    /// Folder portion of the path (everything up to and including the final '/').
    /// Returns empty for a top-level file.
    member this.Folder =
        let v = this.Value
        let i = v.LastIndexOf '/'
        if i < 0 then "" else v.Substring(0, i + 1)

    /// File-name portion (last segment).
    member this.FileName =
        let v = this.Value
        let i = v.LastIndexOf '/'
        if i < 0 then v else v.Substring(i + 1)

    static member private ContainsTraversal(s: string) =
        let parts = s.Replace('\\', '/').Split('/')
        parts |> Array.exists (fun p -> p = "..")

    static member TryCreate(s: string) : Result<RelativePath, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Relative path cannot be null, empty, or whitespace."
        elif s.StartsWith "/" || s.StartsWith "\\" then
            Error "Relative path cannot start with a directory separator."
        elif RelativePath.ContainsTraversal s then
            Error "Relative path cannot contain a '..' segment."
        else
            Ok (RelativePath (s.Replace('\\', '/')))

    static member Create(s: string) : RelativePath =
        match RelativePath.TryCreate s with
        | Ok p -> p
        | Error msg -> raise (ArgumentException(msg, nameof s))
