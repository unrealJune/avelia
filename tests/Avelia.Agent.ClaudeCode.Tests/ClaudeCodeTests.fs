module Avelia.Agent.ClaudeCode.Tests.ClaudeCodeTests

open Xunit
open Avelia.Agent.ClaudeCode

[<Fact>]
let ``defaults points at claude executable`` () =
    Assert.Equal("claude", AgentSettings.defaults.ExecutablePath)
