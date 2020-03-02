open Core_kernel
open Libbackend
open Libcommon


let () =
    Nocrypto_entropy_unix.initialize () ;
    let current_canvases =
      Serialize.current_hosts ()
    in
    try
      current_canvases
      |> List.map ~f:(fun canvas_name ->
        try
         (canvas_name, Canvas.load_and_resave canvas_name)
        with e ->
         (canvas_name, Error [(Log.dump e)])
      )
     |> List.filter_map ~f:(fun (canvas_name, res) ->
       (match res with
       | Error err -> Some (canvas_name, err)
       | _ -> None)
     )
     |> List.iter ~f:(fun (canvas_name, err) ->
       let err = String.concat ~sep:", " err in
       let msg =
        "Error priming cache for canvas " ^ canvas_name ^ ": " ^ err
       in
       Caml.print_endline msg
     )
    with e ->
     Caml.print_endline ("Uh oh " ^ Log.dump e)
