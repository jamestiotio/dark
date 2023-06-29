/// StdLib for handling JS-WASM interactions via WASM'd Darklang code
module Wasm.Libs.Editor

open System

open Prelude
open Tablecloth

open LibExecution.RuntimeTypes
open LibExecution.StdLib.Shortcuts
open Wasm.EvalHelpers


let types : List<BuiltInType> = []
let constants : List<BuiltInConstant> = []


type Editor =
  { Types : List<UserType.T>
    Functions : List<UserFunction.T>
    Constants : List<UserConstant.T>
    CurrentState : Dval }


/// A "user program" that can be executed by the interpreter
/// (probably generated by AI).
type UserProgramSource =
  { types : List<UserType.T>
    fns : List<UserFunction.T>
    constants : List<UserConstant.T>
    exprs : List<Expr> }

// this is client.dark, loaded and live, along with some current state
let mutable editor : Editor =
  { Types = []; Functions = []; Constants = []; CurrentState = DUnit }

let fns : List<BuiltInFn> =
  [ { name = fn' [ "WASM"; "Editor" ] "getState" 0
      typeParams = [ "state" ]
      parameters = [ Param.make "unit" TUnit "" ]
      returnType = TResult(TVariable "a", TString)
      description = "TODO"
      fn =
        (function
        | _, [ _typeParam ], [ DUnit ] ->
          uply {
            try
              let state = editor.CurrentState
              // TODO: assert that the type matches the given typeParam
              return DResult(Ok state)
            with e ->
              return
                $"Error getting state: {e.Message}" |> DString |> Error |> DResult
          }
        | _ -> incorrectArgs ())
      sqlSpec = NotQueryable
      previewable = Impure
      deprecated = NotDeprecated }


    { name = fn' [ "WASM"; "Editor" ] "setState" 0
      typeParams = [ "a" ]
      parameters = [ Param.make "state" (TVariable "a") "" ]
      returnType = TResult(TVariable "a", TString)
      description = "TODO"
      fn =
        (function
        | _, [ _typeParam ], [ v ] ->
          uply {
            // TODO: verify that the type matches the given typeParam
            editor <- { editor with CurrentState = v }
            return DResult(Ok v)
          }
        | _ -> incorrectArgs ())
      sqlSpec = NotQueryable
      previewable = Impure
      deprecated = NotDeprecated }


    { name = fn' [ "WASM"; "Editor" ] "callJSFunction" 0
      typeParams = []
      parameters =
        [ Param.make "functionName" TString ""
          Param.make "args" (TList TString) "" ]
      returnType = TResult(TUnit, TString)
      description =
        "Calls a JS function with the given args.
        Note: this will throw an exception if the function doesn't exist in the webworker that hosts the Dark runtime"
      fn =
        (function
        | _, _, [ DString functionName; DList args ] ->
          let args =
            args
            |> List.fold (Ok []) (fun agg item ->
              match agg, item with
              | (Error err, _) -> Error err
              | (Ok l, DString arg) -> Ok(arg :: l)
              | (_, notAString) ->
                // this should be a DError, not a "normal" error
                $"Expected args to be a `List<String>`, but got: {LibExecution.DvalReprDeveloper.toRepr notAString}"
                |> Error)
            |> Result.map (fun pairs -> List.rev pairs)

          match args with
          | Ok args ->
            uply {
              try
                do Wasm.WasmHelpers.callJSFunction functionName args
                return DResult(Ok DUnit)
              with e ->
                return
                  $"Error calling {functionName} with provided args: {e.Message}"
                  |> DString
                  |> Error
                  |> DResult
            }
          | Error err -> Ply(DResult(Error(DString err)))
        | _ -> incorrectArgs ())
      sqlSpec = NotQueryable
      previewable = Impure
      deprecated = NotDeprecated }


    { name = fn' [ "WASM"; "Editor" ] "evalUserProgram" 0
      typeParams = []
      parameters = [ Param.make "program" TString "" ]
      returnType = TResult(TString, TString)
      description = "Eval a user program (probably generated by AI)"
      fn =
        (function
        | _, _, [ DString sourceJson ] ->
          uply {
            let source = Json.Vanilla.deserialize<UserProgramSource> sourceJson

            let stdLib =
              LibExecution.StdLib.combine
                [ StdLibExecution.StdLib.contents; Wasm.Libs.HttpClient.contents ]
                []
                []

            let! result =
              let expr = exprsCollapsedIntoOne source.exprs
              let state =
                getStateForEval stdLib source.types source.fns source.constants
              let inputVars = Map.empty
              LibExecution.Execution.executeExpr state inputVars expr

            match result with
            | DError(_source, err) -> return DResult(Error(DString err))
            | result ->
              return
                LibExecution.DvalReprDeveloper.toRepr result
                |> DString
                |> Ok
                |> DResult
          }
        | _ -> incorrectArgs ())
      sqlSpec = NotQueryable
      previewable = Impure
      deprecated = NotDeprecated }

    ]

let contents = (fns, types, constants)
