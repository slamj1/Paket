/// Contains methods to handle lockfiles.
module Paket.LockFile

open System
open System.IO

/// [omit]
let formatVersionRange (version : VersionRange) = 
    match version with
    | Minimum v -> ">= " + v.ToString()
    | Specific v -> v.ToString()
    | Latest -> ">= 0"
    | Range(_, v1, v2, _) -> ">= " + v1.ToString() + ", < " + v2.ToString()

/// [omit]
let extractErrors (resolved : PackageResolution) = 
    let errors = 
        resolved
        |> Seq.map (fun x ->
            match x.Value with
            | Resolved _ -> ""
            | Conflict(c1,c2) ->
                let d1 = 
                    match c1 with
                    | FromRoot _ -> "Dependencies file"
                    | FromPackage d -> 
                        let v1 = 
                            match d.Defining.VersionRange with
                            | Specific v -> v.ToString()
                        d.Defining.Name + " " + v1
     
                let d2 = 
                    match c2 with
                    | FromRoot _ -> "Dependencies file"
                    | FromPackage d -> 
                        let v1 = 
                            match d.Defining.VersionRange with
                            | Specific v -> v.ToString()
                        d.Defining.Name + " " + v1

                sprintf "%s depends on%s  %s (%s)%s%s depends on%s  %s (%s)" 
                        d1 Environment.NewLine c1.Referenced.Name (formatVersionRange c1.Referenced.VersionRange) Environment.NewLine 
                        d2 Environment.NewLine c2.Referenced.Name (formatVersionRange c2.Referenced.VersionRange) 
            )
        |> Seq.filter ((<>) "")
    String.Join(Environment.NewLine,errors)


/// [omit]
let serializePackages (resolved : PackageResolution) = 
    let sources = 
        resolved
        |> Seq.map (fun x ->
            match x.Value with
            | Resolved package -> 
                match package.Source with
                | Nuget url -> url,package
                | LocalNuget path -> path,package
            | Conflict(c1,c2) ->
                traceErrorfn "%A %A" c1 c2
                failwith ""  // TODO: trace all errors
            )
        |> Seq.groupBy fst

    let all = 
        [ yield "NUGET"
          for source, packages in sources do
              yield "  remote: " + source
              yield "  specs:"
              for _,package in packages do
                  yield sprintf "    %s (%s)" package.Name (package.Version.ToString()) 
                  for name,v in package.DirectDependencies do
                      yield sprintf "      %s (%s)" name (formatVersionRange v)]
    
    String.Join(Environment.NewLine, all)

let serializeSourceFiles (files:SourceFile list) =
    seq {
        yield "GITHUB"
        for (owner,project), files in files |> Seq.groupBy(fun f -> f.Owner, f.Project) do
            yield sprintf "  remote: %s/%s" owner project
            yield "  specs:"
            for file in files do
                let path = file.Path.TrimStart '/'
                match file.Commit with
                | Some commit -> yield sprintf "    %s (%s)" path commit
                | None -> yield sprintf "    %s" path
    }
    |> fun all -> String.Join(Environment.NewLine, all)

type private ParseState =
    { RepositoryType : string option
      Remote : string option
      Packages : ResolvedPackage list
      SourceFiles : SourceFile list }

let private (|Remote|NugetPackage|NugetDependency|SourceFile|Spec|RepositoryType|Blank|) (state, line:string) =
    match (state.RepositoryType, line.Trim()) with
    | _, "NUGET" -> RepositoryType "NUGET"
    | _, "GITHUB" -> RepositoryType "GITHUB"
    | _, _ when String.IsNullOrWhiteSpace line -> Blank
    | _, trimmed when trimmed.StartsWith "remote:" -> Remote (trimmed.Substring(trimmed.IndexOf(": ") + 2))
    | _, trimmed when trimmed.StartsWith "specs:" -> Spec
    | _, trimmed when line.StartsWith "      " ->
        let parts = trimmed.Split '(' 
        NugetDependency (parts.[0].Trim(),parts.[1].Replace("(", "").Replace(")", "").Trim())
    | Some "NUGET", trimmed -> NugetPackage trimmed
    | Some "GITHUB", trimmed -> SourceFile trimmed
    | Some _, _ -> failwith "unknown Repository Type."
    | _ -> failwith "unknown lock file format."

/// Parses a Lock file from lines
let Parse(lines : string seq) =
    let remove textToRemove (source:string) = source.Replace(textToRemove, "")
    let removeBrackets = remove "(" >> remove ")"
    ({ RepositoryType = None; Remote = None; Packages = []; SourceFiles = [] }, lines)
    ||> Seq.fold(fun state line ->
        match (state, line) with
        | Remote remoteSource -> { state with Remote = Some remoteSource }
        | Spec | Blank -> state
        | RepositoryType repoType -> { state with RepositoryType = Some repoType }
        | NugetPackage details ->
            match state.Remote with
            | Some remote ->
                let parts = details.Split ' '
                let version = parts.[1] |> removeBrackets
                { state with Packages = { Source = PackageSource.Parse remote
                                          Name = parts.[0]
                                          DirectDependencies = []
                                          Version = SemVer.parse version } :: state.Packages }
            | None -> failwith "no source has been specified."
        | NugetDependency (name, version) ->
            match state.Packages with
            | currentPackage :: otherPackages -> 
                { state with
                    Packages = { currentPackage with
                                    DirectDependencies = [name, Latest] |> List.append currentPackage.DirectDependencies
                                } :: otherPackages }
            | [] -> failwith "cannot set a dependency - no package has been specified."
        | SourceFile details ->
            match state.Remote |> Option.map(fun s -> s.Split '/') with
            | Some [| owner; project |] ->
                let path, commit = match details.Split ' ' with
                                   | [| filePath; commit |] -> filePath, Some (commit |> removeBrackets)
                                   | [| filePath |] -> filePath, None
                                   | _ -> failwith "invalid file source details."
                { state with
                    SourceFiles = { Commit = commit
                                    Owner = owner
                                    Project = project
                                    Path = path } :: state.SourceFiles }
            | _ -> failwith "invalid remote details."
        )
    |> fun state -> List.rev state.Packages, List.rev state.SourceFiles

/// Analyzes the dependencies from the Dependencies file.
let Create(force, dependenciesFilename) =     
    tracefn "Parsing %s" dependenciesFilename
    let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFilename
    dependenciesFile.Resolve(force, Nuget.NugetDiscovery), dependenciesFile.RemoteFiles

/// Updates the Lock file with the analyzed dependencies from the Dependencies file.
let Update(force, dependenciesFilename, lockFile) = 
    let packageResolution, remoteFiles = Create(force, dependenciesFilename)
    let errors = extractErrors packageResolution
    if errors = "" then
        let output = String.Join(Environment.NewLine, serializePackages (packageResolution), serializeSourceFiles remoteFiles)
        File.WriteAllText(lockFile, output)
        tracefn "Locked version resolutions written to %s" lockFile
    else
        failwith <| "Could not resolve dependencies." + Environment.NewLine + errors
