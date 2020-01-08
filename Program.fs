// Learn more about F# at http://fsharp.org

open System
open System.Linq
open System.Text
open System.Net.WebSockets
open System.Text.Json
open System.Net.Http
open System.Net.Http.Headers

[<CLIMutable>]
type Login = {
    Username : string
    Password : string
}

[<CLIMutable>]
type LoginData = {
    AuthToken : string
    UserId : string
}

[<CLIMutable>]
type LoginResponse = {
     Status: string
     Data: LoginData
}

[<CLIMutable>]
type RoomId = { _id : string }

[<CLIMutable>]
type Rooms = {
     Update: RoomId array 
}

let uid () = Guid.NewGuid().ToString("N")

let toJson obj = 
    JsonSerializer.Serialize(obj, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

let toJsonAsBytes obj = 
    JsonSerializer.SerializeToUtf8Bytes(obj, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

let client = ClientWebSocket()

let sleep ms = 
    let waiter = async { do! Async.Sleep ms }
    waiter |> Async.RunSynchronously

let rec wait c =
  sleep 100  
  let r = c ()
  match r with
    | true -> ()
    | false -> wait c

let forSend (txt : string) = ArraySegment<byte>(Encoding.UTF8.GetBytes(txt))
let receive (data : ArraySegment<byte>) (res : WebSocketReceiveResult) = 
    Encoding.UTF8.GetString(data.Slice(0, res.Count).ToArray())

let postLogin url (login : Login) = 
    async {
        use cl = HttpClient()
        use content =  ByteArrayContent(toJsonAsBytes login)
        content.Headers.Add("Content-Type", ["application/json;charset=utf-8"])
        let! r = cl.PostAsync(Uri(url), content ) |> Async.AwaitTask
        let! resp = r.Content.ReadAsStringAsync() |> Async.AwaitTask
        return JsonSerializer.Deserialize<LoginResponse>(resp, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
    } |> Async.RunSynchronously
    
let getData url (loginInfo : LoginResponse) = 
    async {
        use cl = HttpClient()
        cl.DefaultRequestHeaders.Add("X-User-Id",loginInfo.Data.UserId)     
        cl.DefaultRequestHeaders.Add("X-Auth-Token", loginInfo.Data.AuthToken)
        cl.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/x-www-form-urlencoded"))
        return! cl.GetStringAsync(Uri(url)) |> Async.AwaitTask
    } |> Async.RunSynchronously

let getChannels (loginInfo : LoginResponse) =
    getData "http://localhost:3000/api/v1/channels.list" loginInfo

let getGroups (loginInfo : LoginResponse) =
    getData "http://localhost:3000/api/v1/groups.list" loginInfo

let getRooms (loginInfo : LoginResponse) =
    getData "http://localhost:3000/api/v1/rooms.get" loginInfo

[<EntryPoint>]
let main argv =

    let login = {
        Username = "blevaka"
        Password = "eQsUmwedKnFYj3c"
    }
    let res = postLogin "http://localhost:3000/api/v1/login" login
    printfn "%A" res
    let txtRooms = (getRooms res)
    printfn "%s" txtRooms
 
    let rooms = JsonSerializer.Deserialize<Rooms>(txtRooms, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
    printfn "ROOMS += %A" rooms

    let url2 = "ws://localhost:3000/websocket"
    
    client.ConnectAsync(Uri(url2), Async.DefaultCancellationToken)
    |> Async.AwaitTask |> ignore

    printfn "state = %A" client.State

    let waitOpen () = wait (fun _ -> client.State = WebSocketState.Open)
    
    waitOpen ()

    let sendText txt =
        client.SendAsync( forSend txt, WebSocketMessageType.Text, true, Async.DefaultCancellationToken) |> Async.AwaitTask |> ignore
    sendText """{
    "msg": "connect",
    "version": "1",
    "support": ["1"]
    }"""  
    
    //sleep 1000

    printfn "state = %A" client.State
    let receiveMessage = Event<string>()
    
    receiveMessage.Publish 
        |> Event.filter ( fun e -> e.Contains("ping"))
        |> Event.add (fun msg -> 
            printfn "%A PING: %A" DateTime.Now msg
            sendText """{"msg" : "pong"}""")
    
    receiveMessage.Publish 
        |> Event.filter ( fun e -> not (e.Contains("ping")))
        |> Event.add (fun msg -> 
            printfn "%A ANY: %A" DateTime.Now msg)

    let my = "__my_messages__"
    sendText (sprintf "{\"msg\":\"sub\",\"id\": \"%s\",\"name\": \"stream-room-messages\",\"params\":[\"%s\", false]}" (uid()) my)
    
    for r in rooms.Update do
        sendText (sprintf "{\"msg\":\"sub\",\"id\": \"%s\",\"name\": \"stream-room-messages\",\"params\":[\"%s\", false]}" (uid()) r._id)
(*
    sendText """{
    "msg": "method",
    "method": "subscriptions/get",
    "id": "46867882",
    "params": [ { "$date": 0 } ]
    }"""
  *)  
    while true do
        let arr = Array.init 4096 (fun _ -> 0uy)
        let buffer = ArraySegment<byte>(arr)
        let result = client.ReceiveAsync(buffer, Async.DefaultCancellationToken) |> Async.AwaitTask |> Async.RunSynchronously
        do receiveMessage.Trigger (receive buffer result)
    printfn "state = %A" client.State
    
    if client.State = WebSocketState.Open  then 
        client.CloseAsync(WebSocketCloseStatus.Empty, "" , Async.DefaultCancellationToken) |> Async.AwaitTask |> ignore
    else ()    
    exit 0