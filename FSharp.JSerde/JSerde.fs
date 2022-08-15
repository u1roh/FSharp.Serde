module FSharp.JSerde.JSerde
open Microsoft.FSharp.Reflection
open FSharp.Data

let custom<'a> (serialize: 'a -> JsonValue) (deserialize: JsonValue -> 'a) =
  { new ICustomSerializer with
      member _.TargetType = typeof<'a>
      member _.Serialize obj = serialize (obj :?> 'a)
      member _.Deserialize json = deserialize json :> obj }

exception UnsupportedType of System.Type

let private bindingFlags = System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic

let rec serialize (custom : Serializer option) (obj: obj) =
  if isNull obj then JsonValue.Null else
  match custom |> Option.bind (fun c -> c.Serialize obj) with
  | Some json -> json
  | _ ->
    match obj with
    | :? bool as value -> JsonValue.Boolean value
    | :? int as value -> JsonValue.Number (decimal value)
    | :? decimal as value -> JsonValue.Number value
    | :? float as value -> JsonValue.Float value
    | :? string as value -> JsonValue.String value
    | :? System.Array as a -> Array.init a.Length (fun i -> a.GetValue i |> serialize custom) |> JsonValue.Array
    | _ ->
      let t = obj.GetType()
      if FSharpType.IsTuple t then
        FSharpValue.GetTupleFields obj
        |> Array.map (serialize custom)
        |> JsonValue.Array
      elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
        let case, fields = FSharpValue.GetUnionFields (obj, obj.GetType(), false)
        if case.Name = "Some"
          then serialize custom fields[0]
          else JsonValue.Null
      elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
        serializeList custom obj
        |> List.toArray
        |> JsonValue.Array
      elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
        obj :?> System.Collections.IEnumerable
        |> Seq.cast<obj>
        |> Seq.map (fun pair ->
          let t = pair.GetType()
          let get name = (t.GetProperty name).GetValue pair |> serialize custom
          let key = get "Key" |> function JsonValue.String s -> s | json -> string json
          key, get "Value")
        |> Seq.toArray
        |> JsonValue.Record
      elif FSharpType.IsRecord (t, bindingFlags) then
        FSharpType.GetRecordFields (t, bindingFlags)
        |> Array.map (fun prop -> prop.Name, FSharpValue.GetRecordField (obj, prop) |> serialize custom)
        |> JsonValue.Record
      elif FSharpType.IsUnion (t, bindingFlags) then
        let case, fields = FSharpValue.GetUnionFields (obj, t, true)
        if (FSharpType.GetUnionCases (t, true)).Length = 1 then // single case union
          match fields with
          | [||] -> JsonValue.String case.Name
          | [| item |] -> serialize custom item
          | array -> array |> Array.map (serialize custom) |> JsonValue.Array
        else
          match fields with
          | [||] -> JsonValue.String case.Name
          | [| item |] -> JsonValue.Record [| case.Name, serialize custom item |]
          | array -> JsonValue.Record [| case.Name, array |> Array.map (serialize custom) |> JsonValue.Array |]
      elif t.GetMethod ("Parse", [| typeof<string> |]) |> isNull |> not then
        obj.ToString() |> JsonValue.String
      else
        raise (UnsupportedType t)

and private serializeList (custom: Serializer option) obj =
  let case, fields = FSharpValue.GetUnionFields (obj, obj.GetType(), false)
  if case.Name = "Cons" then
    serialize custom fields[0] :: serializeList custom fields[1]
  else
    []

exception TypeMismatched of System.Type * JsonValue

let rec private deserializeByType (custom: Serializer option) (t: System.Type) (json: JsonValue) : obj =
  let fail () = TypeMismatched (t, json) |> raise
  match custom |> Option.bind (fun c -> c.Deserialize (t, json)) with
  | Some obj -> obj
  | _ ->
    match DesUtil.classify t, json with
    | DesUtil.Array (elmType, createArray), JsonValue.Array src ->
      src
      |> Array.map (fun obj -> deserializeByType custom elmType obj)
      |> createArray
    | DesUtil.Option (elmType, createOption), json ->
      if json = JsonValue.Null
        then None
        else deserializeByType custom elmType json |> Some 
      |> createOption
    | DesUtil.List (elmType, createList), JsonValue.Array src ->
      src
      |> Array.map (deserializeByType custom elmType)
      |> createList
    | DesUtil.Map (keyType, valueType, createMap), JsonValue.Record src ->
      src
      |> Array.map (fun (key, value) ->
        let key =
          JsonValue.TryParse key
          |> Option.map (deserializeByType custom keyType)
          |> Option.defaultValue key
        let value = deserializeByType custom valueType value
        key, value)
      |> createMap
    | DesUtil.SingleCaseUnion (fields, create), json ->
      match fields, json with
      | [| field |], _ -> create [| deserializeByType custom field.PropertyType json |]
      | fields, JsonValue.Array a when fields.Length = a.Length ->
        Array.zip fields a
        |> Array.map (fun (field, json) -> deserializeByType custom field.PropertyType json)
        |> create
      | _ -> fail()
    | DesUtil.Union (cases, create), JsonValue.String name ->
      cases
      |> Array.tryFind (fun case -> case.Name = name && case.GetFields().Length = 0)
      |> function Some case -> create case [||] | _ -> fail()
    | DesUtil.Union (cases, create), JsonValue.Record [| name, json |] ->
      cases
      |> Array.tryFind (fun case -> case.Name = name)
      |> Option.bind (fun case ->
          match case.GetFields(), json with
          | [| field |], _ -> create case [| deserializeByType custom field.PropertyType json |] |> Some
          | fields, JsonValue.Array a when fields.Length = a.Length -> 
            Array.zip fields a
            |> Array.map (fun (field, json) -> deserializeByType custom field.PropertyType json)
            |> create case |> Some
          | _ -> None)
      |> function Some obj -> obj | _ -> fail ()
    | DesUtil.Record (fields, create), JsonValue.Record src ->
      fields
      |> Array.map (fun field -> field.PropertyType, src |> Array.find (fst >> (=) field.Name))
      |> Array.map (fun (elmType, (_, obj)) -> deserializeByType custom elmType obj)
      |> create
    | DesUtil.Tuple (elmTypes, create), JsonValue.Array src when src.Length = elmTypes.Length ->
      Array.zip elmTypes src
      |> Array.map (fun (t, json) -> deserializeByType custom t json)
      |> create
    | DesUtil.String,   JsonValue.String s -> s :> obj
    | DesUtil.Bool,     JsonValue.Boolean b -> b :> obj
    | DesUtil.Int,      JsonValue.Number n -> int n :> obj
    | DesUtil.Float,    JsonValue.Number n -> float n :> obj
    | DesUtil.Decimal,  JsonValue.Number n -> n :> obj
    | DesUtil.Int,      JsonValue.Float n -> int n :> obj
    | DesUtil.Float,    JsonValue.Float n -> n :> obj
    | DesUtil.Decimal,  JsonValue.Float n -> decimal n :> obj
    | DesUtil.Parsable parse, JsonValue.String s -> parse s
    | _ -> fail ()

let deserialize<'a> custom json = deserializeByType custom typeof<'a> json :?> 'a
