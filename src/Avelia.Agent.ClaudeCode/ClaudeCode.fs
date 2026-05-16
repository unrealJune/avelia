namespace Avelia.Agent.ClaudeCode

type AgentSettings =
    { ExecutablePath: string
      Model: string }

module AgentSettings =
    let defaults =
        { ExecutablePath = "claude"
          Model = "claude-opus-4-7" }
