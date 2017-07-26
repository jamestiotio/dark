module View exposing (view)

import Char
import Dict exposing (Dict)
import Set
import Json.Decode as JSD
import Json.Decode.Pipeline as JSDP
import List.Extra

import Svg
import Svg.Attributes as SA

import Html
import Html.Attributes as Attrs
import Html.Events as Events


import Consts
import Types exposing (..)
import Util exposing (deMaybe)

view : Model -> Html.Html Msg
view model =
  -- TODO: recalculate this using Tasks
  let (w, h) = Util.windowSize ()
  in
    Html.div
      [Attrs.id "grid"]
      [ (Svg.svg
           [ SA.width (toString w) , SA.height (toString <| h - 60)]
           (viewCanvas model))
      -- , viewRepl model.inputValue
      , viewErrors model.errors
      ]

viewRepl value = Html.form [
                  Events.onSubmit (ReplSubmitMsg)
                 ] [
                  Html.input [ Attrs.id Consts.replID
                             , Events.onInput ReplInputMsg
                             , Attrs.value value
                             , Attrs.autocomplete False
                             ] []
                 ]

-- TODO: CSS this onto the bottom
viewErrors errors = Html.div [Attrs.id "darkErrors"] <| (Html.text "Err: ") :: (List.map Html.text errors)
viewLive live = Html.div [Attrs.id "darkLive"]
                [(Html.text <| "LiveValue: " ++
                    (case live of
                       Nothing -> "n/a"
                       Just (val, tipe) -> val ++ " (" ++ tipe ++ ")"))]

viewCanvas : Model -> List (Svg.Svg Msg)
viewCanvas m =
    let allNodes = List.indexedMap (\i n -> viewNode m n i) (Util.orderedNodes m)
        edges = List.map (viewEdge m) m.edges
        mDragEdge = viewDragEdge m.drag m.lastPos
        dragEdge = case mDragEdge of
                     Just de -> [de]
                     Nothing -> []
        click = viewEntry m
    in svgDefs :: svgArrowHead :: click :: (allNodes ++ dragEdge ++ edges)

placeHtml : Pos -> Html.Html Msg -> Svg.Svg Msg
placeHtml pos html =
  Svg.foreignObject
    [ SA.x (toString pos.x)
    , SA.y (toString pos.y)
    ]
    [ html ]

viewClick pos =
  Svg.circle [ SA.r "10"
             , SA.cx (toString pos.x)
             , SA.cy (toString pos.y)
             , SA.fill "#333"] []

viewEntry : Model -> Html.Html Msg
viewEntry m =

  let
    viewForm = Html.form [
                Events.onSubmit (EntrySubmitMsg)
               ] [
                Html.input [ Attrs.id Consts.entryID
                           , Events.onInput EntryInputMsg
                           , Attrs.value m.entryValue
                           , Attrs.autocomplete False
                           ] []
               ]

    -- width
    width = Attrs.style [("width", "100px")]

    -- inner node
    inner = Html.div
            [width, Attrs.class "inner"]
            [viewForm]


    -- outer node wrapper
    classes = "selection function node"

    wrapper = Html.span
              [ Attrs.class classes, width]
              [ inner ]
  in
    placeHtml m.lastPos wrapper



nodeWidth : Node -> Int
nodeWidth n =
  let
    slimChars = Set.fromList Consts.narrowChars
    len name =
      name
        |> synonym
        |> String.toList
        |> List.map (\c -> if Set.member c slimChars then 0.5 else 1)
        |> List.sum
    nameMultiple = case n.tipe of
                     Datastore -> 2
                     Page -> 2.2
                     _ -> 1
    ln = [nameMultiple * len n.name]
    lf = List.map (\(n,t) -> len n + len t + 3) n.fields
    charWidth = List.foldl max 2 (ln ++ lf)
    width = charWidth * 10
  in
    round(width)

nodeHeight : Node -> Int
nodeHeight n =
  case n.tipe of
    Datastore -> Consts.nodeHeight * ( 1 + (List.length n.fields))
    _ -> Consts.nodeHeight

nodeSize node =
  (nodeWidth node , nodeHeight node)
paramOffset : Node -> String -> Pos
paramOffset node param =
  let
    index = deMaybe (List.Extra.elemIndex param node.parameters)
  in
    {x=index*10, y=-2}

synonym x =
  case x of
    "get_field" -> " ."
    "wrap" -> " :"
    _ -> x

-- TODO: use a Dict
slotIsConnected : Model -> ID -> ParamName -> Bool
slotIsConnected m target param =
  let matches edge = (edge.target == target) && (edge.targetParam == param)
      all = List.map matches m.edges
  in
    List.any identity all

-- TODO: Allow selecting an edge, then highlight it and show its source and target
-- TODO: If there are default parameters, show them inline in the node body
-- TODO: could maybe use little icons to denote the params
viewNode : Model -> Node -> Int -> Html.Html Msg
viewNode m n i =
  let
      -- params
      slotHandler name = (decodeClickEvent (DragSlotStart n name))
      connected name = if slotIsConnected m n.id name
                       then "connected"
                       else "disconnected"
      viewParam name = Html.span
                       [ Events.on "mousedown" (slotHandler name)
                       , Attrs.title name
                       , Attrs.class (connected name)]
                       [Html.text "◉"]

      -- header
      viewHeader = Html.div
                   [Attrs.class "header"]
                     [ Html.span
                         [Attrs.class "parameters"]
                         (List.map viewParam n.parameters)
                     , Html.span
                         [Attrs.class "letter"]
                         [Html.text (Util.int2letter i)]
                     ]

      -- heading
      name = synonym n.name
      heading = Html.span
                [ Attrs.class "name"]
                [ Html.text name ]

      -- fields (in list)
      viewField (name, tipe) = [ Html.text (name ++ " : " ++ tipe)
                               , Html.br [] []]
      viewFields = List.map viewField n.fields

      -- list
      list = if viewFields /= []
             then
               [Html.span
                 [Attrs.class "list"]
                 (List.concat viewFields)]
             else []

       -- width
      width = Attrs.style [("width",
                            (toString (nodeWidth n)) ++ "px")]
      -- events
      events =
        [ Events.onClick (NodeClick n)
        , Events.on "mousedown" (decodeClickEvent (DragNodeStart n))
        , Events.onMouseUp (DragSlotEnd n)]

      -- inner node
      inner = Html.div
              (width :: (Attrs.class "inner") :: events)
              (viewHeader :: heading :: list)


      -- outer node wrapper
      selected = case m.cursor of
                       Just id -> id == n.id
                       _ -> False
      selectedCl = if selected then ["selected"] else []
      class = String.toLower (toString n.tipe)
      classes = String.join " " (["node", class] ++ selectedCl)

      wrapper = Html.span
                [ Attrs.class classes, width]
                [ inner ]
  in
    placeHtml n.pos wrapper

-- Our edges should be a lineargradient from "darker" to "arrowColor". SVG
-- gradients are weird, they don't allow you specify based on the line
-- direction, but only on the absolute direction. So we define 8 linear
-- gradients, one for each 45 degree angle/direction. We define this in terms of
-- "rise over run" (eg like you'd calculate a slope). Then we translate the x,y
-- source/target positions into (rise,run) in the integer range [-1,0,1].
svgDefs =
  Svg.defs []
    [ linearGradient 0 1
    , linearGradient 1 1
    , linearGradient 1 0
    , linearGradient 1 -1
    , linearGradient 0 -1
    , linearGradient -1 -1
    , linearGradient -1 0
    , linearGradient -1 1
    ]

coord2id rise run =
  "linear-rise" ++ toString rise ++ "-run" ++ toString run


linearGradient : Int -> Int -> Svg.Svg a
linearGradient rise run =
  -- edge case, linearGradients use positive integers
  let (x1, x2) = if run == -1 then (1,0) else (0, run)
      (y1, y2) = if rise == -1 then (1,0) else (0, rise)
  in
    Svg.linearGradient
      [ SA.id (coord2id rise run)
      , SA.x1 (toString x1)
      , SA.y1 (toString y1)
      , SA.x2 (toString x2)
      , SA.y2 (toString y2)]
    [ Svg.stop [ SA.offset "0%"
               , SA.stopColor Consts.edgeGradColor] []
    , Svg.stop [ SA.offset "100%"
               , SA.stopColor Consts.edgeColor] []]

dragEdgeStyle =
  [ SA.strokeWidth Consts.dragEdgeSize
  , SA.stroke Consts.dragEdgeStrokeColor
  ]

edgeStyle x1 y1 x2 y2 =
  -- edge case: We don't want to use a vertical gradient for really tiny rises,
  -- or it'll just be one color (same for the run). 20 seems enough to avoid
  -- this, empirically.
  let rise = if y2 - y1 > 20 then 1 else if y2 - y1 < -20 then -1 else 0
      run = if x2 - x1 > 20 then 1 else if x2 - x1 < -20 then -1 else 0
      -- edge case: (0,0) is nothing; go in range.
      amendedRise = if (rise,run) == (0,0)
                    then if y2 - y1 > 0 then 1 else -1
                    else rise
  in [ SA.strokeWidth Consts.edgeSize
     , SA.stroke ("url(#" ++ coord2id amendedRise run ++ ")")
     , SA.markerEnd "url(#triangle)"
     ]

svgLine : Pos -> Pos -> List (Svg.Attribute Msg) -> Svg.Svg Msg
svgLine p1 p2 attrs =
  -- edge case: avoid zero width/height lines, or they won't appear
  let ( x1, y1, x2_, y2_ ) = (p1.x, p1.y, p2.x, p2.y)
      x2 = if x1 == x2_ then x2_ + 1 else x2_
      y2 = if y1 == y2_ then y2_ + 1 else y2_
  in
  Svg.line
    ([ SA.x1 (toString x1)
     , SA.y1 (toString y1)
     , SA.x2 (toString x2)
     , SA.y2 (toString y2)
     ] ++ attrs)
    []

viewDragEdge : Drag -> Pos -> Maybe (Svg.Svg Msg)
viewDragEdge drag currentPos =
  case drag of
    DragNode _ _ -> Nothing
    NoDrag -> Nothing
    DragSlot id param mStartPos ->
      Just <|
        svgLine mStartPos
                currentPos
                dragEdgeStyle

viewEdge : Model -> Edge -> Svg.Svg Msg
viewEdge m {source, target, targetParam} =
    let mSourceN = Dict.get (deID source) m.nodes
        mTargetN = Dict.get (deID target) m.nodes
        (sourceN, targetN) = (deMaybe mSourceN, deMaybe mTargetN)
        targetPos = targetN.pos
        (sourceW, sourceH) = nodeSize sourceN

        pOffset = paramOffset targetN targetParam
        (tnx, tny) = (targetN.pos.x + pOffset.x, targetN.pos.y + pOffset.y)

        -- find the shortest line and link to there
        joins = [ (tnx, tny) -- topleft
                , (tnx + 5, tny) -- topright
                , (tnx, tny + 5) -- bottomleft
                , (tnx + 5, tny + 5) -- bottomright
                ]
        sq x = toFloat (x*x)
        -- ideally to source pos would be at the bottom of the node. But, the
        -- positioning of the node is a little bit off because css, and nodes
        -- with parameters are in different relative offsets than nodes without
        -- parameters. This makes it hard to line things up exactly.
        spos = { x = sourceN.pos.x + (sourceW // 2)
               , y = sourceN.pos.y + (sourceH // 2)}

        join = List.head
               (List.sortBy (\(x,y) -> sqrt ((sq (spos.x - x)) + (sq (spos.y - y))))
                  joins)
        (tx, ty) = deMaybe join
    in svgLine
      spos
      {x=tx,y=ty}
      (edgeStyle spos.x spos.y tx ty)

svgArrowHead =
  Svg.marker [ SA.id "triangle"
             , SA.viewBox "0 0 10 10"
             , SA.refX "4"
             , SA.refY "5"
             , SA.markerUnits "strokeWidth"
             , SA.markerWidth "4"
             , SA.markerHeight "4"
             , SA.orient "auto"
             , SA.fill Consts.edgeColor
             ]
    [Svg.path [SA.d "M 0 0 L 5 5 L 0 10 z"] []]

decodeClickEvent : (MouseEvent -> a) -> JSD.Decoder a
decodeClickEvent fn =
  let toA : Int -> Int -> Int -> a
      toA px py button =
        fn {pos= {x=px, y=py}, button = button}
  in JSDP.decode toA
      |> JSDP.required "pageX" JSD.int
      |> JSDP.required "pageY" JSD.int
      |> JSDP.required "button" JSD.int

