module Tests.BwdServer

open Expecto

open LibExecution
open LibBackend
open LibBackend.ProgramSerialization
open BwdServer

module RT = LibExecution.RuntimeTypes

open System.Threading.Tasks
open System.IO
open System.Threading
open System.Net
open System.Net.Sockets
open System.Text.RegularExpressions

let t name =
  testTask $"Httpfiles: {name}" {
    // TODO: This test relies on the server running already. Run the server
    // instead as part of the test suite.
    let toBytes (str : string) = System.Text.Encoding.ASCII.GetBytes str
    let toStr (bytes : byte array) = System.Text.Encoding.ASCII.GetString bytes

    let setHeadersToCRLF (text : byte array) : byte array =
      // We keep our test files with an LF line ending, but the HTTP spec
      // requires headers (but not the body, nor the first line) to have CRLF
      // line endings
      let mutable justSawNewline = false
      let mutable inBody = false
      text
      |> Array.toList
      |> List.collect (fun b ->
           if inBody = false && b = byte '\n' then
             if justSawNewline then inBody <- true
             justSawNewline <- true
             [ byte '\r'; b ]
           else
             justSawNewline <- false
             [ b ])
      |> List.toArray

    let request, expectedResponse, progString, httpMethod, httpRoute =
      let filename = $"tests/httptestfiles/{name}"
      let contents = filename |> System.IO.File.ReadAllBytes |> toStr

      // TODO: use FsRegex instead
      let options = System.Text.RegularExpressions.RegexOptions.Singleline

      let m =
        Regex.Match
          (contents,
           "^\[http-handler (\S+) (\S+)\]\n(.*)\n\[request\]\n(.*)\[response\]\n(.*)$",
           options)

      if not m.Success then failwith $"incorrect format in {name}"
      let g = m.Groups
      g.[4].Value |> toBytes |> setHeadersToCRLF,
      g.[5].Value |> toBytes |> setHeadersToCRLF,
      g.[3].Value,
      g.[1].Value,
      g.[2].Value

    let (source : ProgramTypes.Expr) =
      progString |> FSharpToExpr.parse |> FSharpToExpr.convertToExpr

    let (handler : ProgramTypes.Handler.T) =
      let id = SharedTypes.id

      let ids : ProgramTypes.Handler.ids =
        { moduleID = id 1; nameID = id 2; modifierID = id 3 }

      { tlid = id 7
        ast = source
        spec =
          ProgramTypes.Handler.HTTP
            (route = httpRoute, method = httpMethod, ids = ids) }

    let! ownerID = LibBackend.Account.userIDForUsername "test"
    let! canvasID = LibBackend.Canvas.canvasIDForCanvas ownerID $"test-{name}"

    do! LibBackend.ProgramSerialization.SQL.saveHttpHandlersToCache
          canvasID
          ownerID
          [ ProgramTypes.TLHandler handler ]

    // Web server might not be loaded yet
    let client = new TcpClient()

    let mutable connected = false

    for i in 1 .. 10 do
      try
        if not connected then
          do! client.ConnectAsync("127.0.0.1", 9001)
          connected <- true
      with _ -> do! System.Threading.Tasks.Task.Delay 1000

    let stream = client.GetStream()

    stream.ReadTimeout <- 1000 // responses should be instant, right?

    stream.Write(request, 0, request.Length)

    let length = 10000

    let response = Array.zeroCreate length

    let byteCount = stream.Read(response, 0, length)

    let response = Array.take byteCount response

    stream.Close()

    client.Close()

    let response =
      FsRegEx.replace
        "Date: ..., .. ... .... ..:..:.. ..."
        "Date: XXX, XX XXX XXXX XX:XX:XX XXX"
        (toStr response)

    Expect.equal response (toStr expectedResponse) ""
  }

let testsFromFiles =
  // get all files
  let dir = "tests/httptestfiles/"
  System.IO.Directory.GetFiles(dir, "*")
  |> Array.map (System.IO.Path.GetFileName)
  |> Array.toList
  |> List.map t

let testMany (name : string) (fn : 'a -> 'b) (values : List<'a * 'b>) =
  testList
    name
    (List.mapi (fun i (input, expected) ->
      test $"{name}[{i}]: ({input}) -> {expected}" {
        Expect.equal (fn input) expected "" }) values)

let testMany2 (name : string) (fn : 'a -> 'b -> 'c) (values : List<'a * 'b * 'c>) =
  testList
    name
    (List.mapi (fun i (input1, input2, expected) ->
      test $"{name}[{i}]: ({input1}, {input2}) -> {expected}" {
        Expect.equal (fn input1 input2) expected "" }) values)



let testManyTask (name : string) (fn : 'a -> Task<'b>) (values : List<'a * 'b>) =
  testList
    name
    (List.mapi (fun i (input, expected) ->
      testTask $"{name} - {i}" {
        let! result = fn input
        Expect.equal result expected ""
      }) values)

let unitTests =
  [ testMany
      "sanitizeUrlPath"
      BwdServer.sanitizeUrlPath
      [ ("//", "/")
        ("/foo//bar", "/foo/bar")
        ("/abc//", "/abc")
        ("/abc/", "/abc")
        ("/abc", "/abc")
        ("/", "/")
        ("/abcabc//xyz///", "/abcabc/xyz")
        ("", "/") ]
    testMany
      "ownerNameFromHost"
      LibBackend.Canvas.ownerNameFromHost
      [ ("test-something", "test"); ("test", "test"); ("test-many-hyphens", "test") ]
    testMany
      "routeVariables"
      Http.routeVariables
      [ ("/user/:userid/card/:cardid", [ "userid"; "cardid" ]) ]
    testMany2
      "routeInputServer"
      Http.routeInputVars
      [ ("/hello/:name", "/hello/alice-bob", Some [ "name", RT.DStr "alice-bob" ])
        ("/hello/alice-bob", "/hello/", None)
        ("/user/:userid/card/:cardid",
         "/user/myid/card/0",
         Some [ "cardid", RT.DStr "0"; "userid", RT.DStr "myid" ])
        ("/a/:b/c/d", "/a/b/c/d", Some [ "b", RT.DStr "b" ])
        ("/a/:b/c/d", "/a/b/c", None)
        ("/a/:b", "/a/b/c/d", Some [ "b", RT.DStr "b/c/d" ])
        ("/:a/:b/:c",
         "/a/b/c/d/e",
         Some [ "c", RT.DStr "c/d/e"; "b", RT.DStr "b"; "a", RT.DStr "a" ])
        ("/a/:b/c/d", "/a/b/c/e", None)
        ("/letters:var", "lettersextra", None) ]
    testManyTask
      "canvasNameFromHost"
      BwdServer.canvasNameFromHost
      [ ("test-something.builtwithdark.com", Some "test-something")
        ("my-canvas.builtwithdark.localhost", Some "my-canvas")
        ("builtwithdark.localhost", Some "builtwithdark")
        ("my-canvas.darkcustomdomain.com", Some "my-canvas")
        ("www.microsoft.com", None) ] ]

let tests =
  testList
    "BwdServer"
    [ testList "From files" testsFromFiles; testList "unit tests" unitTests ]