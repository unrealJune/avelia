module Avelia.Agent.Copilot.Tests.ModelMappingTests

open Xunit
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

[<Fact>]
let ``presets map to stable copilot catalog ids`` () =
    Assert.Equal("claude-sonnet-4.5", ModelMapping.toCopilotModelId Sonnet45)
    Assert.Equal("claude-opus-4.1", ModelMapping.toCopilotModelId Opus41)
    Assert.Equal("claude-haiku-4.5", ModelMapping.toCopilotModelId Haiku45)

[<Fact>]
let ``custom model passes through verbatim`` () =
    Assert.Equal("gpt-5-codex", ModelMapping.toCopilotModelId (CustomModel "gpt-5-codex"))

[<Fact>]
let ``blank custom model maps to empty (SDK default)`` () =
    Assert.Equal("", ModelMapping.toCopilotModelId (CustomModel "   "))
    Assert.Equal("", ModelMapping.toCopilotModelId (CustomModel ""))
