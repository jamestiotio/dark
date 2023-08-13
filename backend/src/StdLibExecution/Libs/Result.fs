module StdLibExecution.Libs.Result

open System.Threading.Tasks
open FSharp.Control.Tasks

open System.Numerics

open Prelude
open LibExecution.RuntimeTypes
open LibExecution.StdLib.Shortcuts

module Interpreter = LibExecution.Interpreter
module Errors = LibExecution.Errors

let varOk = TVariable "ok"
let varErr = TVariable "err"
let varA = TVariable "a"
let varB = TVariable "b"
let varC = TVariable "c"

let types : List<BuiltInType> = []
let constants : List<BuiltInConstant> = []

let fn = fn [ "Result" ]

let fns : List<BuiltInFn> =
  [ { name = fn "map2" 0
      typeParams = []
      parameters =
        [ Param.make "result1" (TypeReference.result varA varErr) ""
          Param.make "result2" (TypeReference.result varB varErr) ""
          Param.makeWithArgs
            "fn"
            (TFn(NEList.doubleton varA varB, varC))
            ""
            [ "v1"; "v2" ] ]
      returnType = (TypeReference.result varC varErr)
      description =
        "If both <param result1> is {{Ok <var v1>}} and <param result2> is {{Ok <var
         v2>}}, returns {{Ok (fn <var v1> <var v2>)}} -- the lambda <param fn> is
         applied to <var v1> and <var v2>, and the result is wrapped in {{Ok}}.
        Otherwise, returns the first of <param result1> and <param result2> that is
         an error."
      fn =
        (function
        | state,
          _,
          [ DEnum(_typeName1, _, case1, [ arg1 ])
            DEnum(_typeName2, _, case2, [ arg2 ])
            DFnVal b ] ->
          uply {
            match case1, case2 with
            | "Error", _ -> return Dval.resultError arg1
            | "Ok", "Error" -> return Dval.resultError arg2
            | "Ok", "Ok" ->
              let args = NEList.doubleton arg1 arg2
              let! result = Interpreter.applyFnVal state 0UL b [] args
              return Dval.resultOk result
            | _, _ ->
              return
                Exception.raiseInternal
                  "Invalid Result casenames"
                  [ "case1", case1; "case2", case2 ]
          }
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplemented
      previewable = Pure
      deprecated = NotDeprecated } ]

let contents = (fns, types, constants)
