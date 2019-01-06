module Buckaroo.Tests.Solver

open Xunit
open Buckaroo
open FSharp.Control

open Buckaroo.Console
open Buckaroo.Tasks

type CookBook = List<PackageIdentifier * Set<Version> * Manifest>
type LockBookEntries = List<(string*int) * List<string*int*Set<Version>>>
type LockBook = Map<PackageLock, Lock>
let package name = PackageIdentifier.Adhoc {
  Owner = "test";
  Project = name
}

let br b = Version.Git (GitVersion.Branch b)
let rev (x : int) = Version.Git(GitVersion.Revision (x.ToString()))
let ver (x : int) = Version.SemVer {SemVer.zero with Major = x}

let dep (p : string, c: Constraint) : Buckaroo.Dependency = {
    Package = package p;
    Constraint = c;
    Targets = None
}

let manifest xs = {
  Manifest.zero
    with Dependencies = xs |> List.map dep |> Set.ofList
}

let lockPackage (p, r, vs) : LockedPackage = {
  Versions = Set vs
  Location = PackageLock.GitHub {
    Package = {Owner = "test"; Project = p};
    Revision = r.ToString()
  }
  PrivatePackages = Map.empty
}

let packageLock (p, r) : PackageLock =  PackageLock.GitHub {
  Package = {Owner = "test"; Project = p};
  Revision = r.ToString()
}

let lock deps : Lock = {
  ManifestHash = "";
  Dependencies = Set[]
  Packages = deps
    |> Seq.map (fun (name, r, vs) -> (package name, lockPackage (name, r, vs)))
    |> Map.ofSeq
}

let lockBookOf (entries : LockBookEntries) : LockBook =
  entries
  |> Seq.map (fun (l, deps) -> (packageLock l, lock deps))
  |> Map.ofSeq



type TestingSourceExplorer (cookBook : CookBook, lockBook : LockBook) =
  interface ISourceExplorer with
    member this.FetchVersions (_ : PackageSources) (package: PackageIdentifier) : AsyncSeq<Version>  = asyncSeq {
      yield!
        cookBook
          |> Seq.choose (fun (p, v, _) ->
            if p = package
            then Some (v |> Set.toSeq |> Seq.sortDescending)
            else None)
          |> Seq.collect (id)
          |> AsyncSeq.ofSeq
    }

    member this.FetchLocations (_: PackageSources) (package: PackageIdentifier) (version: Version) : AsyncSeq<PackageLocation> = asyncSeq {
        match package with
        | (Adhoc a) ->
          yield! cookBook
            |> Seq.filter (fun (p, vs, _) -> p = package && vs |> Set.contains version)
            |> Seq.map(fun (_, vs, _) -> vs)
            |> Seq.collect(Set.toList)
            |> Seq.choose(fun x ->
                match x with
                | Version.Git (GitVersion.Revision r) -> Some r
                | SemVer v -> Some (v.Major.ToString())
                | _ -> None)
            |> Seq.map(fun r ->
                PackageLocation.GitHub {
                  Package = { Owner = a.Owner; Project = a.Project };
                  Revision = r
                })
            |> AsyncSeq.ofSeq
        | _ -> raise <| new System.SystemException "package not found"
    }

    member this.LockLocation (location : PackageLocation) : Async<PackageLock> = async {
      let x =
        match location with
        | PackageLocation.GitHub g -> Some (PackageLock.GitHub g)
        | _ -> None
      return x.Value
    }

    member this.FetchManifest (lock : PackageLock) : Async<Manifest> = async {
      let x =
        match lock with
        | PackageLock.GitHub g ->
          cookBook
          |> List.filter (fun (p, v, _) -> p = package g.Package.Project && v.Contains(g.Revision.ToString() |> int |> ver))
          |> List.map (fun (_, _, m) -> m)
          |> List.tryHead
        | _ -> None

      return x |> Option.get
    }

    member this.FetchLock (lock : PackageLock) : Async<Lock> = async {
      return lockBook |> Map.find lock
    }


let solve (cookBook : CookBook) (lockBookEntries : LockBookEntries) root style =
    let lockBook = lockBookOf lockBookEntries
    let console = new ConsoleManager(LoggingLevel.Debug);
    let context : TaskContext = {
      Console = console;
      DownloadManager = DownloadManager(console, "/tmp");
      GitManager = new GitManager(new GitCli(console), "/tmp");
      SourceExplorer = TestingSourceExplorer(cookBook, lockBook)
    }

    Buckaroo.Solver.solve
      context root style
      (lockBook |> Map.tryFind (packageLock ("root", 0)))

let getLockedRev (p : string) (r: Resolution) =
  match r with
  | Ok solution ->
    let (resolved, _) = solution.Resolutions.[package p]
    match resolved.Lock with
    | PackageLock.GitHub g -> g.Revision
    | _ -> ""
  | _ -> ""
()

let isOk (r: Resolution) =
  match r with
  | Ok _ -> true
  | _ -> false

[<Fact>]
let ``Solver handles simple case`` () =
  let cookBook = [
    (package "a",
      Set[ver 2; br "a"],
      manifest [("b", Exactly (ver 1) )])
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (ver 1) )])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
  ]

  let root = manifest [("a", Exactly (br "a") )]
  let solution =
    solve
      cookBook [] root
      ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("2", getLockedRev "a" solution)
  Assert.Equal ("1", getLockedRev "b" solution)
  ()

[<Fact>]
let ``Solver can backtrack to resolve simple conflicts`` () =
  let cookBook = [
    (package "a",
      Set[ver 2; br "a"],
      manifest [("b", Exactly (ver 2) )])
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (ver 1) )])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
  ]

  let root = manifest [("a", Exactly (br "a") )]
  let solution =
    solve cookBook [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("1", getLockedRev "a" solution)
  Assert.Equal ("1", getLockedRev "b" solution)
  ()


[<Fact>]
let ``Solver can compute version intersections`` () =

  let root = manifest [
    ("a", Exactly (ver 1) )
    ("b", Exactly (ver 1) )
  ]
  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a") )])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("1", getLockedRev "a" solution)
  Assert.Equal ("1", getLockedRev "b" solution)
  ()

[<Fact>]
let ``Solver can compute intersection of branches`` () =

  let root = manifest [
    ("a", All [Exactly (br "b"); Exactly (br "a")])
  ]

  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [])
    (package "a",
      Set[ver 2; br "a"],
      manifest [])
    (package "a",
      Set[ver 3; br "a"; br "b"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("3", getLockedRev "a" solution)
  ()

[<Fact>]
let ``Solver fails if package cant satisfy all constraints`` () =

  let root = manifest [
    ("a", Exactly (br "a"))
    ("b", Exactly (br "b"))
  ]

  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a"))])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
    (package "b",
      Set[ver 2; br "b"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.False (isOk solution)
  ()


[<Fact>]
let ``Solver picks package that satisfies all constraints`` () =

  let root = manifest [
    ("a", Exactly (br "a"))
    ("b", Exactly (br "b"))
  ]

  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a"))])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
    (package "b",
      Set[ver 2; br "b"],
      manifest [])
    (package "b",
      Set[ver 3; br "a"; br "b"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("3", getLockedRev "b" solution)
  ()


[<Fact>]
let ``Solver deduces that a package can satisfy multiple constraints`` () =

  let root = manifest [
    ("a", Exactly (br "a"))
    ("b", Exactly (br "b"))
  ]

  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a"))])
    (package "b",
      Set[ver 2; br "a"],
      manifest [])
    (package "b",
      Set[ver 2; br "b"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("2", getLockedRev "b" solution)
  ()


[<Fact>]
let ``Solver handles negated constraints also`` () =

  let root = manifest [
    ("a", Exactly (br "a"))
    ("b", Any [Exactly (br "a"); Exactly (br "b")])
  ]

  let spec = [
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Complement (Exactly (br "a")))])
    (package "b",
      Set[ver 2; br "a"],
      manifest [])
    (package "b",
      Set[ver 2; br "b"],
      manifest [])
    (package "a",
      Set[ver 3; br "a"],
      manifest [])
    (package "b",
      Set[ver 4; br "b"],
      manifest [])
  ]

  let solution =
    solve spec [] root ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("4", getLockedRev "b" solution)
  ()

[<Fact>]
let ``Solver uses lockfile as hint in Quick`` () =
  let cookBook = [
    (package "a",
      Set[ver 2; br "a"],
      manifest [("b", Exactly (br "a") )])
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a") )])
    (package "b",
      Set[ver 2; br "a"],
      manifest [])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
  ]

  let lockBook = [
    (("root", 0), [
      ("a", 1, Set[ver 1; br "a"])
    ])
    (("a", 1), [
      ("b", 1, Set[ver 1; br "a"])
    ])
  ]


  let root = manifest [("a", Exactly (br "a") )]
  let solution =
    solve
      cookBook lockBook root
      ResolutionStyle.Quick
      |> Async.RunSynchronously

  Assert.Equal ("1", getLockedRev "a" solution)
  Assert.Equal ("1", getLockedRev "b" solution)
  ()

[<Fact>]
let ``Solver doesnt use lockfile as hint in Upgrade`` () =
  let cookBook = [
    (package "a",
      Set[ver 2; br "a"],
      manifest [("b", Exactly (br "a") )])
    (package "a",
      Set[ver 1; br "a"],
      manifest [("b", Exactly (br "a") )])
    (package "b",
      Set[ver 2; br "a"],
      manifest [])
    (package "b",
      Set[ver 1; br "a"],
      manifest [])
  ]

  let lockBook = [
    (("root", 0), [
      ("a", 1, Set[ver 1; br "a"])
    ])
    (("a", 1), [
      ("b", 1, Set[ver 1; br "a"])
    ])
  ]


  let root = manifest [("a", Exactly (br "a") )]
  let solution =
    solve
      cookBook lockBook root
      ResolutionStyle.Upgrading
      |> Async.RunSynchronously

  Assert.Equal ("2", getLockedRev "a" solution)
  Assert.Equal ("2", getLockedRev "b" solution)
  ()