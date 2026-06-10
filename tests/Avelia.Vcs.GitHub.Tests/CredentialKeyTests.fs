module Avelia.Vcs.GitHub.Tests.CredentialKeyTests

open Xunit
open Avelia.Vcs.GitHub.Auth

// ----------------------------------------------------------------------------
//  Unit tests for the "avelia:github:..." key convention.
//
//  Regression guard for the bug where the manually-pasted-PAT sentinel slot
//  (avelia:github:_pat) was mistaken for a signed-in account: account
//  enumeration drove GitHubClientProvider to deserialize the raw PAT as a JSON
//  token blob, crashing PR creation with "Token blob is not valid JSON". Real
//  GitHub logins can never begin with '_', so the leading-underscore suffix
//  namespace is reserved for non-account sentinel slots and must not be
//  enumerated as an account.
// ----------------------------------------------------------------------------

[<Fact>]
let ``tryParseGitHubAccount returns the login for a normal account key`` () =
    match CredentialKey.tryParseGitHubAccount "avelia:github:octocat" with
    | ValueSome login -> Assert.Equal("octocat", login)
    | ValueNone -> Assert.Fail "Expected the login to parse."

[<Fact>]
let ``tryParseGitHubAccount ignores the manual-PAT sentinel slot`` () =
    // avelia:github:_pat stores a raw token string, not a serialized account
    // token, so it must not surface as a signed-in account.
    Assert.Equal(ValueNone, CredentialKey.tryParseGitHubAccount "avelia:github:_pat")

[<Fact>]
let ``tryParseGitHubAccount ignores any underscore-prefixed sentinel suffix`` () =
    Assert.Equal(ValueNone, CredentialKey.tryParseGitHubAccount "avelia:github:_future-slot")

[<Fact>]
let ``tryParseGitHubAccount returns ValueNone for a non-github key`` () =
    Assert.Equal(ValueNone, CredentialKey.tryParseGitHubAccount "avelia:test:something")

[<Fact>]
let ``tryParseGitHubAccount returns ValueNone for null`` () =
    Assert.Equal(ValueNone, CredentialKey.tryParseGitHubAccount null)

[<Fact>]
let ``forGitHubAccount then tryParseGitHubAccount round-trips (lowercased)`` () =
    let key = CredentialKey.forGitHubAccount "OctoCat"

    match CredentialKey.tryParseGitHubAccount key with
    | ValueSome login -> Assert.Equal("octocat", login)
    | ValueNone -> Assert.Fail "Expected the login to round-trip."
