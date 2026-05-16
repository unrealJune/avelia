namespace Avelia.Core.Abstractions

type AveliaError =
    | NotFound of resource: string
    | Validation of message: string
    | Unauthorized
    | Conflict of message: string
    | Network of message: string
    | Internal of message: string
