namespace Avelia.Core.Abstractions

open System

// ============================================================================
//  Primitives are <c>[<Struct>]</c> single-case DUs with a private constructor.
//  Because they are structs, C# callers can write <c>default(BranchName)</c>
//  and obtain an instance whose underlying string is <c>null</c> — bypassing
//  <c>TryCreate</c>. Every <c>.Value</c> getter therefore normalizes a null
//  inner string to <c>""</c> so downstream code never NREs. An empty
//  <c>.Value</c> is the "uninitialized" signal; no successful
//  <c>TryCreate</c> ever produces one.
//
//  Validation rejects a leading <c>-</c> in addition to the named
//  metacharacters. A primitive whose <c>.Value</c> starts with <c>-</c> would
//  be interpreted as an option by any CLI we pass it to (git.exe, ConPTY
//  children, etc.) even with <c>UseShellExecute = false</c>. Catching here
//  is defence in depth that lifts the guarantee out of every call site.
// ============================================================================

/// A git branch name. Wraps a string with validation so callers can't mix it
/// up with arbitrary paths or labels. Uses a private constructor; obtain via
/// <c>BranchName.TryCreate</c> (safe) or <c>BranchName.Create</c> (throws on
/// invalid input — use for test fixtures / design data).
[<Struct>]
type BranchName =
    private
    | BranchName of string

    member this.Value =
        let (BranchName s) = this
        if String.IsNullOrEmpty s then "" else s

    override this.ToString() = this.Value

    /// Validate and construct. Returns <c>Ok</c> on success or <c>Error</c> with
    /// a human-readable reason. Validation mirrors git's ref-name rules at the
    /// 80% level — empty/whitespace and obvious metacharacters are rejected;
    /// uncommon edge cases (consecutive dots, trailing slash, etc.) are not
    /// enforced here and would surface as a git error downstream.
    static member TryCreate(s: string) : Result<BranchName, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Branch name cannot be null, empty, or whitespace."
        elif s.StartsWith "-" then
            // Defence in depth — a leading '-' would otherwise be parsed as
            // an option by git when we pass the value as a positional argv.
            Error "Branch name cannot start with '-'."
        elif s.StartsWith "/" || s.EndsWith "/" then
            Error "Branch name cannot start or end with '/'."
        elif s.Contains ".." then
            Error "Branch name cannot contain '..'."
        elif s.IndexOfAny([| ' '; '\t'; '\n'; ':'; '?'; '['; '*'; '\\'; '~'; '^' |]) >= 0 then
            Error "Branch name contains an invalid character."
        else
            Ok(BranchName s)

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

    member this.Value =
        let (RepoPath p) = this
        if String.IsNullOrEmpty p then "" else p

    override this.ToString() = this.Value

    static member private ContainsTraversal(s: string) =
        let parts = s.Replace('\\', '/').Split('/')
        parts |> Array.exists (fun p -> p = "..")

    /// Validate and construct. Rejects empty paths, paths containing a
    /// <c>..</c> segment, and paths starting with <c>-</c> (which would be
    /// interpreted as a CLI option downstream). Defence in depth — the
    /// storage layer also validates, but catching here keeps untrusted UI
    /// input from ever creating a domain value.
    static member TryCreate(s: string) : Result<RepoPath, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Repository path cannot be null, empty, or whitespace."
        elif s.StartsWith "-" then
            Error "Repository path cannot start with '-'."
        elif RepoPath.ContainsTraversal s then
            Error "Repository path cannot contain a '..' segment."
        else
            Ok(RepoPath s)

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

    member this.Value =
        let (RelativePath p) = this
        if String.IsNullOrEmpty p then "" else p

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
        elif s.StartsWith "-" then
            Error "Relative path cannot start with '-'."
        elif s.StartsWith "/" || s.StartsWith "\\" then
            Error "Relative path cannot start with a directory separator."
        elif RelativePath.ContainsTraversal s then
            Error "Relative path cannot contain a '..' segment."
        else
            Ok(RelativePath(s.Replace('\\', '/')))

    static member Create(s: string) : RelativePath =
        match RelativePath.TryCreate s with
        | Ok p -> p
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// Raw git commit SHA, lowercase hex. Wraps a string so a commit ID is never
/// confused with an arbitrary identifier. Stored full-length (40 chars for
/// SHA-1, 64 for SHA-256); display abbreviation is a renderer concern.
[<Struct>]
type CommitId =
    private
    | CommitId of string

    member this.Value =
        let (CommitId s) = this
        if String.IsNullOrEmpty s then "" else s

    override this.ToString() = this.Value

    static member private IsHexChar(c: char) =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    static member TryCreate(s: string) : Result<CommitId, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Commit id cannot be null, empty, or whitespace."
        else
            let normalized = s.ToLowerInvariant()
            // SHA-1 = 40 hex chars; SHA-256 (git 2.42+) = 64. Accept either.
            if normalized.Length <> 40 && normalized.Length <> 64 then
                Error "Commit id must be 40 (SHA-1) or 64 (SHA-256) hex characters."
            elif not (normalized |> Seq.forall CommitId.IsHexChar) then
                Error "Commit id must contain only hexadecimal characters."
            else
                Ok(CommitId normalized)

    static member Create(s: string) : CommitId =
        match CommitId.TryCreate s with
        | Ok c -> c
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// Plaintext commit message. Wrapped so a stray label or branch name can't
/// stand in for a message at the boundary. Multi-line content is allowed; the
/// only validation is non-empty.
[<Struct>]
type CommitMessage =
    private
    | CommitMessage of string

    member this.Value =
        let (CommitMessage s) = this
        if String.IsNullOrEmpty s then "" else s

    override this.ToString() = this.Value

    static member TryCreate(s: string) : Result<CommitMessage, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Commit message cannot be null, empty, or whitespace."
        else
            Ok(CommitMessage s)

    static member Create(s: string) : CommitMessage =
        match CommitMessage.TryCreate s with
        | Ok m -> m
        | Error msg -> raise (ArgumentException(msg, nameof s))

/// Name of a git remote (typically <c>origin</c>). Single-case DU so a remote
/// can't be confused with a branch or arbitrary string. Same character rules
/// as a refname's first component: no whitespace, no <c>:</c>, no <c>/</c>
/// (a remote name is a single path segment), no leading <c>-</c>.
[<Struct>]
type Remote =
    private
    | Remote of string

    member this.Value =
        let (Remote s) = this
        if String.IsNullOrEmpty s then "" else s

    override this.ToString() = this.Value

    static member TryCreate(s: string) : Result<Remote, string> =
        if String.IsNullOrWhiteSpace s then
            Error "Remote name cannot be null, empty, or whitespace."
        elif s.StartsWith "-" then
            Error "Remote name cannot start with '-'."
        elif s.IndexOfAny([| ' '; '\t'; '\n'; '/'; ':'; '\\' |]) >= 0 then
            Error "Remote name contains an invalid character."
        else
            Ok(Remote s)

    static member Create(s: string) : Remote =
        match Remote.TryCreate s with
        | Ok r -> r
        | Error msg -> raise (ArgumentException(msg, nameof s))

    /// The default git remote name. Convenience for the common case.
    static member Origin: Remote = Remote "origin"
