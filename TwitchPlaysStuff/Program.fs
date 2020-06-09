module TwitchPlaysStuff.Program

open System
open System.IO
open System.Threading
open System.Collections.Concurrent

open FSharp.Data
open ScpDriverInterface

open TwitchLib.Client
open TwitchLib.Client.Models
open TwitchLib.Communication.Models
open TwitchLib.Communication.Clients

type Configuration = JsonProvider<"./config.json">
type Mapping = JsonProvider<"./mappings.json">

let makeControlMapping (mapping: Mapping.Root[]) =
    mapping
    |> Array.groupBy (fun entry -> entry.Messages)
    |> Array.collect (fun (keys, value) ->
        let value =
            value
            |> Array.map (fun entry -> entry.Handler)

        keys
        |> Array.map (fun key -> key, value)
    )
    |> Map.ofArray

let thread (action: unit -> unit) =
    let worker = Thread action
    worker.IsBackground <- true
    worker.Start()

let bus = new ScpBus()
let controller = X360Controller()

let minValue, maxValue = -32768s, 32767s
let report () =
    bus.Report (1, controller.GetReport()) |> ignore

let messages = ConcurrentQueue<string>()

let handleChatMessage (mapping: Map<string, Mapping.Handler[]>) (message: string) =
    let clamp value minimum maximum =
        if value < minimum then minimum
        elif value > maximum then maximum
        else value
    let short (value: int) = int16 (clamp value -32768 32767)

    let prepareState (state: Mapping.Before) =
        // Left stick
        match state.LeftStickX with
        | Some value ->
            controller.LeftStickX <- short value
        | None -> ()
        match state.LeftStickY with
        | Some value ->
            controller.LeftStickY <- short value
        | None -> ()

        // Right stick
        match state.RightStickX with
        | Some value ->
            controller.RightStickX <- short value
        | None -> ()
        match state.RightStickY with
        | Some value ->
            controller.RightStickY <- short value
        | None -> ()

        // Triggers

        match state.LeftTrigger with
        | Some value ->
            controller.LeftTrigger <- if value then 255uy else 0uy
        | None -> ()
        match state.RightTrigger with
        | Some value ->
            controller.RightTrigger <- if value then 255uy else 0uy
        | None -> ()

    match mapping.TryFind (message.ToLower()) with
    | Some handlers ->
        for handler in handlers do
            prepareState handler.Before
           
        report()

        for handler in handlers do
            handler.After |> Option.iter prepareState

    | None -> ()

let handleChatMessages (mapping: Map<string, Mapping.Handler[]>) () =
    let mutable message = ""

    while true do       
        if messages.TryDequeue(&message) then
            handleChatMessage mapping message

let connectToTwitch (configuration: Configuration.Root) () =
    let credentials = ConnectionCredentials(configuration.Username, configuration.AccessToken)
    let clientOptions =
        ClientOptions(
            MessagesAllowedInPeriod = configuration.Throtteling.MessagesAllowedInPeriod,
            ThrottlingPeriod = TimeSpan.FromSeconds (float configuration.Throtteling.PeriodInSeconds)
        )
    let socket = WebSocketClient clientOptions
    let client = TwitchClient socket

    client.Initialize(credentials, configuration.Channel)

    client.OnMessageReceived.Add (fun event ->
        let chatMessage = event.ChatMessage
        messages.Enqueue(chatMessage.Message)
    )

    client.Connect()

[<EntryPoint>]
let main _ =
    let configuration = Configuration.Load "./config.json"
    let mapping = Mapping.Load "./mappings.json"
    let controlMapping = makeControlMapping mapping

    bus.UnplugAll() |> ignore

    if bus.PlugIn 1 then
        printfn "Plugged in virtual controller"

        thread (handleChatMessages controlMapping)
        thread (connectToTwitch configuration)

        printfn "Press enter to exit."
        Console.ReadLine() |> ignore
        
        bus.UnplugAll() |> ignore
        0
    else
        printfn "Failed to plug in virtual controller."
        printfn "Press enter to exit."
        Console.ReadLine() |> ignore
        
        1