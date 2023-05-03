module Parser.Package

open FSharp.Compiler.Syntax

open Prelude
open Tablecloth

module PT = LibExecution.ProgramTypes

open Utils

type PackageModule = { fns : List<PT.Package.Fn> }

let emptyModule = { fns = [] }


/// Update a CanvasModule by parsing a single F# let binding
/// Depending on the attribute present, this may add a user function, a handler, or a DB
let parseLetBinding
  (modules : List<string>)
  (letBinding : SynBinding)
  : PT.Package.Fn =
  match modules with
  | owner :: modules ->
    let modules = NonEmptyList.ofList modules
    ProgramTypes.PackageFn.fromSynBinding owner modules letBinding
  | _ ->
    Exception.raiseInternal
      "Expected owner, package, and at least 1 other modules"
      [ "modules", modules; "binding", letBinding ]


let parseDecls
  (modules : List<string>)
  (decls : List<SynModuleDecl>)
  : PackageModule =
  List.fold
    emptyModule
    (fun m decl ->
      match decl with
      | SynModuleDecl.Let (_, bindings, _) ->
        let fns = List.map (parseLetBinding modules) bindings
        { m with fns = m.fns @ fns }

      // | SynModuleDecl.Types (defns, _) ->
      //   List.fold m (fun m d -> parseTypeDefn m d) defns

      | _ -> Exception.raiseInternal $"Unsupported declaration" [ "decl", decl ])
    decls

let parseModule (moduleDecl : SynModuleOrNamespace) : PackageModule =
  match moduleDecl with
  | SynModuleOrNamespace (names, _, _, decls, _, _, _, _, _) ->
    let names = List.map string names
    decls |> parseDecls names


let parseFromFile (filename : string) : PackageModule =
  let parsedAsFSharp =
    filename |> System.IO.File.ReadAllText |> parseAsFSharpSourceFile

  match parsedAsFSharp with
  | ParsedImplFileInput (_, _, _, _, _, modules, _, _, _) ->
    let fns =
      modules |> List.map parseModule |> List.map (fun m -> m.fns) |> List.concat
    { fns = fns }
