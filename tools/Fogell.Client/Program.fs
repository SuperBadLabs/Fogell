module Fogell.Client.Program

[<EntryPoint>]
let main args =
    use client = Client.createHttpClient ()
    Client.run client System.Console.Out System.Console.Error args
    |> fun operation -> operation.GetAwaiter().GetResult()
