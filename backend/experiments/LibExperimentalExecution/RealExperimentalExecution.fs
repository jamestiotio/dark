module LibExperimentalExecution.RealExperimentalExecution

// For executing code with the appropriate production "real" execution, setting
// traces, stdlib, etc, appropriately. Used by most of the executables.

open FSharp.Control.Tasks
open System.Threading.Tasks

open Prelude
open Tablecloth

module PT = LibExecution.ProgramTypes
module RT = LibExecution.RuntimeTypes
module PT2RT = LibExecution.ProgramTypesToRuntimeTypes
module AT = LibExecution.AnalysisTypes
module Exe = LibExecution.Execution
module Interpreter = LibExecution.Interpreter

open LibBackend

let (stdlibFns, stdlibTypes, stdlibConstants) =
  LibExecution.StdLib.combine
    [ StdLibExecution.StdLib.contents
      StdLibCloudExecution.StdLib.contents
      StdLibExperimental.StdLib.contents ]
    []
    []


let packageFns : Lazy<Task<Map<RT.FQFnName.PackageFnName, RT.PackageFn.T>>> =
  lazy
    (task {
      let! packages = PackageManager.allFunctions ()

      return
        packages
        |> List.map (fun (f : PT.PackageFn.T) ->
          (f.name |> PT2RT.FQFnName.PackageFnName.toRT, PT2RT.PackageFn.toRT f))
        |> Map.ofList
    })

let packageTypes : Lazy<Task<Map<RT.FQTypeName.PackageTypeName, RT.PackageType.T>>> =
  lazy
    (task {
      let! packages = PackageManager.allTypes ()

      return
        packages
        |> List.map (fun (t : PT.PackageType.T) ->
          (t.name |> PT2RT.FQTypeName.PackageTypeName.toRT, PT2RT.PackageType.toRT t))
        |> Map.ofList
    })

let packageConstants
  : Lazy<Task<Map<RT.FQConstantName.PackageConstantName, RT.PackageConstant.T>>> =
  lazy
    (task {
      let! packages = PackageManager.allConstants ()

      return
        packages
        |> List.map (fun (c : PT.PackageConstant.T) ->
          (c.name |> PT2RT.FQConstantName.PackageConstantName.toRT,
           PT2RT.PackageConstant.toRT c))
        |> Map.ofList
    })

let libraries : Lazy<Task<RT.Libraries>> =
  lazy
    (task {
      let! packageFns = Lazy.force packageFns
      let! packageTypes = Lazy.force packageTypes
      let! packageConstants = Lazy.force packageConstants
      // TODO: this keeps a cached version so we're not loading them all the time.
      // Of course, this won't be up to date if we add more functions. This should be
      // some sort of LRU cache.
      return
        { stdlibTypes = stdlibTypes |> Map.fromListBy (fun typ -> typ.name)
          stdlibFns = stdlibFns |> Map.fromListBy (fun fn -> fn.name)
          stdlibConstants = stdlibConstants |> Map.fromListBy (fun c -> c.name)
          packageFns = packageFns
          packageTypes = packageTypes
          packageConstants = packageConstants }
    })

let createState
  (traceID : AT.TraceID.T)
  (tlid : tlid)
  (program : RT.ProgramContext)
  (tracing : RT.Tracing)
  : Task<RT.ExecutionState> =
  task {
    let! libraries = Lazy.force libraries

    let extraMetadata (state : RT.ExecutionState) : Metadata =
      [ "tlid", tlid
        "trace_id", traceID
        "executing_fn_name", state.executingFnName
        "callstack", state.callstack
        "canvasID", program.canvasID ]

    let notify (state : RT.ExecutionState) (msg : string) (metadata : Metadata) =
      let metadata = extraMetadata state @ metadata
      LibService.Rollbar.notify msg metadata

    let sendException (state : RT.ExecutionState) (metadata : Metadata) (exn : exn) =
      let metadata = extraMetadata state @ metadata
      LibService.Rollbar.sendException None metadata exn

    return Exe.createState libraries tracing sendException notify tlid program
  }

type ExecutionReason =
  /// The first time a trace is executed. This means more data should be stored and
  /// more users notified.
  | InitialExecution of HandlerDesc * varname : string * RT.Dval

  /// A reexecution is a trace that already exists, being amended with new values
  | ReExecution

/// Execute handler. This could be the first execution, which will have an
/// ExecutionReason of InitialExecution, and initialize traces and send pushes, or
/// ReExecution, which will update existing traces and not send pushes.
let executeHandler
  (pusherSerializer : Pusher.PusherEventSerializer)
  (canvasID : CanvasID)
  (h : RT.Handler.T)
  (program : RT.ProgramContext)
  (traceID : AT.TraceID.T)
  (inputVars : Map<string, RT.Dval>)
  (reason : ExecutionReason)
  : Task<RT.Dval * Tracing.TraceResults.T> =
  task {
    let tracing = Tracing.create canvasID h.tlid traceID

    match reason with
    | InitialExecution(desc, varname, inputVar) ->
      tracing.storeTraceInput desc varname inputVar
    | ReExecution -> ()

    let! state = createState traceID h.tlid program tracing.executionTracing
    HashSet.add h.tlid tracing.results.tlids
    let! result = Exe.executeExpr state inputVars h.ast
    tracing.storeTraceResults ()

    match reason with
    | ReExecution -> ()
    | InitialExecution _ ->
      if tracing.enabled then
        let tlids = HashSet.toList tracing.results.tlids
        Pusher.push pusherSerializer canvasID (Pusher.NewTrace(traceID, tlids)) None

    return (result, tracing.results)
  }

/// We call this reexecuteFunction because it always runs in an existing trace.
let reexecuteFunction
  (canvasID : CanvasID)
  (program : RT.ProgramContext)
  (callerTLID : tlid)
  (callerID : id)
  (traceID : AT.TraceID.T)
  (rootTLID : tlid)
  (name : RT.FQFnName.T)
  (typeArgs : List<RT.TypeReference>)
  (args : List<RT.Dval>)
  : Task<RT.Dval * Tracing.TraceResults.T> =
  task {
    // FIX - the TLID here is the tlid of the toplevel in which the call exists, not
    // the rootTLID of the trace.
    let tracing = Tracing.create canvasID rootTLID traceID
    let! state = createState traceID callerTLID program tracing.executionTracing
    let! result = Exe.executeFunction state callerID name typeArgs args
    tracing.storeTraceResults ()
    return result, tracing.results
  }


/// Ensure library is ready to be called. Throws if it cannot initialize.
let init () : Task<unit> =
  task {
    let! (_ : RT.Libraries) = Lazy.force libraries
    return ()
  }
