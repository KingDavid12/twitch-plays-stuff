module TwitchPlaysStuff.Program

open System
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

let prepareState (state: Mapping.Before) =
    let clamp value minimum maximum =
        if value < minimum then minimum
        elif value > maximum then maximum
        else value
    let short (value: int) = int16 (clamp value -32768 32767)
    let handleButton button state =
        match state with
        | Some value ->
            if value then
                controller.Buttons <- controller.Buttons ||| button
            else
                controller.Buttons <- controller.Buttons &&& ~~~button
        | None ->
            ()

    // Left stick
    match state.LeftStickX with
    | Some value ->
        controller.LeftStickX <- short (value * 327)
    | None -> ()
    match state.LeftStickY with
    | Some value ->
        controller.LeftStickY <- short (value * 327)
    | None -> ()

    // Right stick
    match state.RightStickX with
    | Some value ->
        controller.RightStickX <- short (value * 327)
    | None -> ()
    match state.RightStickY with
    | Some value ->
        controller.RightStickY <- short (value * 327)
    | None -> ()

    // Triggers

    match state.LeftTrigger.Boolean with
    | Some value ->
        controller.LeftTrigger <- if value then 255uy else 0uy
    | None -> ()
    match state.LeftTrigger.Number with
    | Some value ->
        controller.LeftTrigger <- byte (clamp value 0 255)
    | None -> ()

    match state.RightTrigger.Boolean with
    | Some value ->
        controller.RightTrigger <- if value then 255uy else 0uy
    | None -> ()
    match state.RightTrigger.Number with
    | Some value ->
        controller.RightTrigger <- byte (clamp value 0 255)
    | None -> ()

    // Buttons

    handleButton X360Buttons.A state.A
    handleButton X360Buttons.B state.B

    handleButton X360Buttons.Back state.Back
    handleButton X360Buttons.Start state.Start

    handleButton X360Buttons.Up state.Up
    handleButton X360Buttons.Down state.Down
    handleButton X360Buttons.Left state.Left
    handleButton X360Buttons.Right state.Right

    handleButton X360Buttons.LeftBumper state.LeftBumper
    handleButton X360Buttons.RightBumper state.RightBumper

    handleButton X360Buttons.X state.X
    handleButton X360Buttons.Y state.Y

    handleButton X360Buttons.Logo state.Logo

    handleButton X360Buttons.LeftStick state.LeftStick
    handleButton X360Buttons.RightStick state.RightStick

// Synchronizes access to the virtual controller to make it thread safe without locks.
let actions = MailboxProcessor.Start (fun mailbox ->
    async {
        while true do
            let! message = mailbox.Receive()
            prepareState message
            bus.Report(1, controller.GetReport()) |> ignore
    }
)

let messages = ConcurrentQueue<string>()
let sleepLock = ConcurrentDictionary<Mapping.Handler, byte>(HashIdentity.Structural)

let handleChatMessage (mapping: Map<string, Mapping.Handler[]>) (message: string) =
    Console.Write("Handeling message: ")
    Console.WriteLine(message)

    match mapping.TryFind (message.ToLower()) with
    | Some handlers ->
        for handler in handlers do
            actions.Post handler.Before

            match handler.HoldFor, handler.After with
            | Some delay, Some after ->
                if not (sleepLock.ContainsKey handler) then
                    sleepLock.[handler] <- 0uy

                    async {
                        do! Async.Sleep delay
                        actions.Post after

                        sleepLock.[handler] <- 0uy
                    } |> Async.Start
            | _ -> ()

        for handler in handlers do
            match handler.HoldFor with
            | Some _ -> ()
            | None ->
                handler.After |> Option.iter actions.Post

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
    client.OnConnected.Add (fun _ -> printfn "Connected to Twitch.")

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