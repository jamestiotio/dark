module FuzzTests.All

// This aims to find test cases that violate certain properties that we expect.
// Desired properties include that OCaml Dark programs and functions work the
// same as F# ones, and things related to serialization and output.

open Expecto
open Expecto.ExpectoFsCheck

open Prelude
open TestUtils

module PT = LibBackend.ProgramTypes
module RT = LibExecution.RuntimeTypes
module OCamlInterop = LibBackend.OCamlInterop
module DvalRepr = LibExecution.DvalRepr

open FsCheck

let (.=.) actual expected : bool =
  if actual = expected then
    Expect.equal actual expected ""
    true
  else
    let o = actual.ToString() |> toBytes
    let e = expected.ToString() |> toBytes
    Expect.equal (actual, o) (expected, e) ""
    false


module GeneratorUtils =
  let nonNullString (s : string) : bool = s <> null

  let safeOCamlString (s : string) : bool =
    // We disallow \u0000 in OCaml because postgres doesn't like it, see of_utf8_encoded_string
    s <> null && not (s.Contains('\u0000'))

open GeneratorUtils

let baseConfig : FsCheckConfig =
  { FsCheckConfig.defaultConfig with maxTest = 100000 }

let baseConfigWithGenerator (typ : System.Type) : FsCheckConfig =
  { baseConfig with arbitrary = [ typ ] }

let testProperty (name : string) (x : 'a) : Test =
  testPropertyWithConfig baseConfig name x

let testPropertyWithGenerator (typ : System.Type) (name : string) (x : 'a) : Test =
  testPropertyWithConfig (baseConfigWithGenerator typ) name x



module FQFnName =
  let nameGenerator (first : char list) (other : char list) : Gen<string> =
    gen {
      let! length = Gen.choose (0, 20)
      let! head = Gen.elements first
      let! tail = Gen.arrayOfLength length (Gen.elements other)
      return System.String(Array.append [| head |] tail)
    }

  type Generator =
    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter nonNullString

    static member FQFnName() : Arbitrary<PT.FQFnName.T> =
      let alphaNumeric =
        (List.concat [ [ 'a' .. 'z' ]; [ '0' .. '9' ]; [ 'A' .. 'Z' ]; [ '_' ] ])

      let ownerName : Gen<string> =
        nameGenerator [ 'a' .. 'z' ] (List.concat [ [ 'a' .. 'z' ]; [ '0' .. '9' ] ])

      let packageName = ownerName
      let modName : Gen<string> = nameGenerator [ 'A' .. 'Z' ] alphaNumeric
      let fnName : Gen<string> = nameGenerator [ 'a' .. 'z' ] alphaNumeric
      { new Arbitrary<PT.FQFnName.T>() with
          member x.Generator =
            gen {
              let! owner = ownerName
              let! package = packageName
              let! module_ = modName
              let! function_ = fnName
              let! NonNegativeInt version = Arb.generate<NonNegativeInt>

              return
                { owner = owner
                  package = package
                  module_ = module_
                  function_ = function_
                  version = version }
            } }

  let roundtrip (a : PT.FQFnName.T) : bool = a.ToString() |> PT.FQFnName.parse .=. a

  let tests =
    [ testPropertyWithGenerator typeof<Generator> "roundtripping FQFnName" roundtrip ]


module OCamlInterop =
  open OCamlInterop.Convert
  open OCamlInterop
  open Json.AutoSerialize

  type Generator =
    static member Expr() =
      Arb.Default.Derive()
      |> Arb.mapFilter
           (function
           // make sure we get numbers in our floats
           | other -> other)
           (function
           // characters are not yet supported in OCaml
           | PT.ECharacter _ -> false
           | other -> true)

    static member Pattern() =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           // characters are not yet supported in OCaml
           | PT.PCharacter _ -> false
           | _ -> true)

    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter nonNullString


  let yojsonExprRoundtrip (a : PT.Expr) : bool =
    a
    |> pt2ocamlExpr
    |> serialize
    |> deserialize
    |> ocamlExpr2PT
    |> serialize
    |> deserialize
    |> pt2ocamlExpr
    |> serialize
    |> deserialize
    |> ocamlExpr2PT
    |> serialize
    |> deserialize
    .=. a

  let yojsonHandlerRoundtrip (a : PT.Handler.T) : bool =
    a
    |> pt2ocamlHandler
    |> serialize
    |> deserialize
    |> ocamlHandler2PT a.pos
    |> serialize
    |> deserialize
    |> pt2ocamlHandler
    |> serialize
    |> deserialize
    |> ocamlHandler2PT a.pos
    |> serialize
    |> deserialize
    .=. a

  let binaryHandlerRoundtrip (a : PT.Handler.T) : bool =
    let h = PT.TLHandler a

    h |> toplevelToCachedBinary |> (fun bin -> bin, None) |> toplevelOfCachedBinary
    .=. h

  let binaryExprRoundtrip (pair : PT.Expr * tlid) : bool =
    pair |> exprTLIDPairToCachedBinary |> exprTLIDPairOfCachedBinary .=. pair

  let tests =
    let tp f = testPropertyWithGenerator typeof<Generator> f

    [ tp "roundtripping OCamlInteropBinaryHandler" binaryHandlerRoundtrip
      tp "roundtripping OCamlInteropBinaryExpr" binaryExprRoundtrip
      tp "roundtripping OCamlInteropYojsonHandler" yojsonHandlerRoundtrip
      tp "roundtripping OCamlInteropYojsonExpr" yojsonExprRoundtrip ]

module RoundtrippableDval =
  type Generator =
    static member String() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter (DvalRepr.isRoundtrippableDval false)

  type GeneratorWithBugs =
    static member String() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter (DvalRepr.isRoundtrippableDval true)

  let roundtrip (dv : RT.Dval) : bool =
    dv
    |> DvalRepr.toInternalRoundtrippableV0
    |> DvalRepr.ofInternalRoundtrippableV0
    |> dvalEquality dv

  let isInteroperable (dv : RT.Dval) : bool =
    try
      // What does it mean to interoperate? Ideally, the F# impl would be able
      // to read what the OCaml impl sends it and vice versa. However, because
      // the OCaml side is buggy, and we want to reproduce those bugs exactly
      // (for now), that isn't sufficient. We actually just want to make sure
      // we produce the same thing as they do for the same value. BUT, we don't
      // actually produce the exact same thing, and it's hard to do that for
      // the edge cases we've found. So really we just want to make sure that
      // whatever either side produces, both sides are able to read it and get
      // the same result.
      let bothCanReadOCamlString =
        let ocamlString = dv |> OCamlInterop.toInternalRoundtrippableV0

        ocamlString
        |> OCamlInterop.ofInternalRoundtrippableV0
        |> dvalEquality (DvalRepr.ofInternalQueryableV0 ocamlString)

      let bothCanReadFSharpString =
        let fsString = dv |> DvalRepr.toInternalRoundtrippableV0

        fsString
        |> OCamlInterop.ofInternalRoundtrippableV0
        |> dvalEquality (DvalRepr.ofInternalQueryableV0 fsString)

      if bothCanReadFSharpString && bothCanReadOCamlString then
        true
      else
        printfn
          "%s"
          ($"ocamlStringReadable: {bothCanReadOCamlString}\n"
           + $"fsharpStringReadable: {bothCanReadFSharpString}\n")

        false
    with e ->
      printfn $"Cause exception while fuzzing {e}"
      reraise ()

  let tests =
    [ testPropertyWithGenerator
        typeof<Generator>
        "roundtripping works properly"
        roundtrip
      testPropertyWithGenerator
        typeof<GeneratorWithBugs>
        "roundtrippable is interoperable"
        isInteroperable ]


module Queryable =
  type Generator =
    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter DvalRepr.isQueryableDval

  let dvalReprInternalQueryableV0Roundtrip (dv : RT.Dval) : bool =
    dv
    |> DvalRepr.toInternalQueryableV0
    |> DvalRepr.ofInternalQueryableV0
    |> dvalEquality dv

  let dvalReprInternalQueryableV1Roundtrip (dvm : RT.DvalMap) : bool =
    dvm
    |> DvalRepr.toInternalQueryableV1
    |> DvalRepr.ofInternalQueryableV1
    |> dvalEquality (RT.DObj dvm)


  let tests =
    let tp f = testPropertyWithGenerator typeof<Generator> f

    [ tp "roundtripping InternalQueryable v0" dvalReprInternalQueryableV0Roundtrip
      tp "roundtripping InternalQueryable v1" dvalReprInternalQueryableV1Roundtrip ]

module DeveloperRepr =
  type Generator =
    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    // The format here is only used for errors so it doesn't matter all the
    // much. These are places where we've manually checked the differing
    // outputs are fine.

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           | RT.DFnVal _ -> false
           | RT.DFloat 0.0 -> false
           | RT.DFloat infinity -> false
           | _ -> true)


  let equalsOCaml (dv : RT.Dval) : bool =
    DvalRepr.toDeveloperReprV0 dv .=. OCamlInterop.toDeveloperRepr dv

  let tests =
    [ testPropertyWithGenerator
        typeof<Generator>
        "roundtripping toDeveloperRepr"
        equalsOCaml ]

module EndUserReadable =
  type Generator =
    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    static member FQFnName() : Arbitrary<PT.FQFnName.T> =
      FQFnName.Generator.FQFnName()

  // The format here is used to show users so it has to be exact
  let equalsOCaml (dv : RT.Dval) : bool =
    DvalRepr.toEnduserReadableTextV0 dv .=. OCamlInterop.toEnduserReadableTextV0 dv

  let tests =
    [ testPropertyWithGenerator
        typeof<Generator>
        "roundtripping toEnduserReadable"
        equalsOCaml ]


module PrettyMachineJson =
  open FsCheck

  type Generator =
    static member SafeString() : Arbitrary<string> =
      Arb.Default.String() |> Arb.filter safeOCamlString

    // This should produce identical JSON to the OCaml function or customers will have an unexpected change
    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           | RT.DFnVal _ -> false
           | _ -> true)

  let equalsParsed (dv : RT.Dval) : bool =
    let actual =
      dv
      |> DvalRepr.toPrettyMachineJsonStringV1
      |> Newtonsoft.Json.Linq.JToken.Parse
      |> toString

    let expected =
      dv
      |> OCamlInterop.toPrettyMachineJsonV1
      |> Newtonsoft.Json.Linq.JToken.Parse
      |> toString

    actual .=. expected

  let tests =
    [ testPropertyWithGenerator
        typeof<Generator>
        "roundtripping prettyMachineJson"
        equalsParsed ]


let stillBuggy = testList "still buggy" (List.concat [ OCamlInterop.tests ])

let knownGood =
  testList
    "known good"
    (List.concat [ FQFnName.tests
                   RoundtrippableDval.tests
                   Queryable.tests
                   DeveloperRepr.tests
                   EndUserReadable.tests
                   PrettyMachineJson.tests ])

let tests = testList "FuzzTests" [ knownGood; stillBuggy ]



[<EntryPoint>]
let main args =
  LibBackend.OCamlInterop.Binary.init ()
  runTestsWithCLIArgs [] args tests
