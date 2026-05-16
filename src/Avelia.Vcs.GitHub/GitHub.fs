namespace Avelia.Vcs.GitHub

type RepoCoordinate = { Owner: string; Name: string }

module RepoCoordinate =
    let parse (s: string) =
        match s.Split('/') with
        | [| owner; name |] -> Some { Owner = owner; Name = name }
        | _ -> None
