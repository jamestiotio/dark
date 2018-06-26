module Sync exposing (..)

import List.Extra as LE

-- dark
import Types exposing (..)
import RPC
import Toplevel
import Util

markRequestInModel : Model -> Model
markRequestInModel m =
  let oldSyncState = m.syncState
  in
      { m | syncState =
        { oldSyncState | inFlight = True, ticks = 0 } }

markTickInModel : Model -> Model
markTickInModel m =
  let oldSyncState = m.syncState
  in
      { m | syncState =
        { oldSyncState | ticks = (oldSyncState.ticks + 1) } }

markResponseInModel : Model -> Model
markResponseInModel m =
  let oldSyncState = m.syncState
  in
      { m | syncState =
        { oldSyncState | inFlight = False, ticks = 0 } }


timedOut : SyncState -> Bool
timedOut s =
 (s.ticks % 10) == 0 && s.ticks /= 0

fetch : Model -> (Model, Cmd Msg)
fetch m =
  if (not m.syncState.inFlight)
      || (timedOut m.syncState)
  then
    (markRequestInModel m) ! [RPC.getAnalysisRPC (toAnalyse m)]
  else
    (markTickInModel m) ! []

toAnalyse : Model -> List TLID
toAnalyse m =
  case m.cursorState of
    Selecting tlid _ -> [tlid]
    Entering (Filling tlid _) -> [tlid]
    Dragging tlid _ _ _ -> [tlid]
    _ ->
      let ids = List.map .id (Toplevel.all m) in
      ids
      |> LE.getAt ((Util.random ()) % (List.length ids))
      |> Maybe.map (\e -> [e])
      |> Maybe.withDefault []

