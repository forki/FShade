﻿namespace FSCC.Service

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Interactive.Shell
open Microsoft.FSharp.Compiler.SourceCodeServices
open System
open System.IO
open System.Threading
open System.Text.RegularExpressions
open Aardvark.Base
open FSharp.RegexProvider
open System.Reflection

module Fsi = 

    type CompilerErrorType = Error | Warning

    [<StructuredFormatDisplay("{AsString}")>]
    type CompilerError = { file : string; line : int; col : int; errorType : CompilerErrorType; code : string; message : string } with
        member x.AsString =
            let typeString = match x.errorType with | Error -> "error" | Warning -> "warning"
            sprintf "%s(%d,%d): %s %s: %s" x.file x.line x.col typeString x.code x.message

    [<StructuredFormatDisplay("{AsString}")>]
    type CompilerErrorList = { errors : list<CompilerError> } with
        member x.AsString = 
            x.errors |> List.map (sprintf "%A") |> String.concat "\r\n"

    type private FsiStream() =
        inherit Stream()

        let mutable inner = new MemoryStream()

        member x.Reset() =
            let newInner = new MemoryStream()
            let old = System.Threading.Interlocked.Exchange(&inner, newInner)
            old.Dispose()

        member x.GetString() =
            let arr = inner.ToArray()
            System.Text.ASCIIEncoding.Default.GetString(arr)

        override x.CanRead = inner.CanRead
        override x.CanWrite = inner.CanWrite
        override x.CanSeek = inner.CanSeek
        override x.Length = inner.Length
        override x.Position
            with get() = inner.Position
            and set p = inner.Position <- p
        override x.Flush() = inner.Flush()
        override x.Seek(p, o) = inner.Seek(p, o)
        override x.SetLength(v) = inner.SetLength(v)
        override x.Read(buffer, offset, count) = inner.Read(buffer, offset, count)
        override x.Write(buffer, offset, count) = inner.Write(buffer, offset, count)

        interface IDisposable with
            member x.Dispose() = inner.Dispose()

    type FsiResult<'a> = FsiSuccess of 'a | FsiError of CompilerErrorList

    type private ErrorRx = Regex< @"\((?<line>[0-9]+),(?<col>[0-9]+)\):[ ]+(?<errorType>warning|error)[ ]+(?<code>[a-zA-Z_0-9]+):(?<message>[^$]*)$" >
    type private SplitRx = Regex< @"input\.fsx" >
    type private UselessWsRx = Regex< @"[ \t\r\n][ \t\r\n]+" >

    type CompletionResult = { Glyph : int; Name : string; Description : list<string * string> }

    /// <summary>
    /// Provides access to the F# interactive shell running in the current AppDomain.
    /// All assemblies located in the application's current directory are automatically available.
    /// ATTENTION: This currently only works on developer-machines with the F#-SDK installed.
    /// </summary>
    type Session() =
        let mutable currentNamespaceId = 0

        // Intialize output and input streams
        let sbOut = new FsiStream()
        let sbErr = new FsiStream()
        let inStream = new StringReader("")
        let outStream = new StreamWriter(sbOut)
        let errStream = new StreamWriter(sbErr)

        //input.fsx(9,9): error FS0039: The value or constructor 'a' is not defined
        let errorRx = ErrorRx()
        let splitRx = SplitRx()
        let uselessWsRx = UselessWsRx()

        let parseErrors (err : string) =
        
            let matches = errorRx.Matches err
            let errors = splitRx.Split(err) |> Seq.choose (fun e ->
                    let m = errorRx.Match e
                    if m.Success then
                        { file = "input.fsx"
                          line = System.Int32.Parse m.line.Value
                          col = System.Int32.Parse m.col.Value
                          errorType = match m.errorType.Value with | "error" -> Error | _ -> Warning
                          code = m.code.Value
                          message = m.message.Value.Replace("\r", "").Replace('\n', ' ') } |> Some
                    else
                        None
                )
            errors |> Seq.toList

        let getError() =
            let str = sbErr.GetString()
            sbErr.Reset()
            { errors = str |> parseErrors }

        let getOutput() =
            let str = sbOut.GetString()
            sbOut.Reset()
            str

        let sync = obj()
        let mutable fsiSession = None

        let initSession() =

            let refAsmDir, fsiDir =
                match System.Environment.OSVersion.Platform with
                    | PlatformID.Unix -> "/usr/local/","/usr/local"
                    | _ -> @"C:\Program Files (x86)\Reference Assemblies\Microsoft\FSharp\3.0\Runtime\v4.0",
                           @"C:\Program Files (x86)\Microsoft SDKs\F#\3.0\Framework\v4.0"

            let compilerFiles = 
                match Environment.OSVersion.Platform with
                    | PlatformID.Unix -> [ "lib/mono/4.5/FSharp.Build.dll"; "lib/mono/4.5/FSharp.Compiler.dll"; "lib/mono/4.5/fsiAnyCPU.exe"; "lib/mono/4.5/fsc.exe"; "lib/mono/4.5/FSharp.Compiler.Interactive.Settings.dll" ]
                    | _ -> [ "FSharp.Build.dll"; "FSharp.Compiler.dll"; "FsiAnyCPU.exe"; "FsiAnyCPU.exe.config"; "Fsc.exe"; "FSharp.Compiler.Interactive.Settings.dll" ]

            let refFiles = 
                match Environment.OSVersion.Platform with
                    | PlatformID.Unix -> ["lib/mono/4.5/FSharp.Core.optdata"; "lib/mono/4.5/FSharp.Core.sigdata"; "policy.2.3.FSharp.Core.dll"; "pub.config"; Path.Combine("Type Providers", "FSharp.Data.TypeProviders.dll")]
                    | _ -> ["FSharp.Core.optdata"; "FSharp.Core.sigdata"; "lib/mono/4.0/policy.2.3.FSharp.Core.dll"; "lib/mono/4.5/FSharp.Data.TypeProviders.dll"]
        
            let copyFile (source : string) (target : string) =
                let target = Path.GetFileName(target)
                if not <| File.Exists target then
                    let d = Path.GetDirectoryName target
                    if d <> "" && not <| Directory.Exists d then
                        Directory.CreateDirectory d |> ignore

                    File.Copy(source, target, true)

            compilerFiles |> List.iter (fun f ->
                let p = Path.Combine(fsiDir, f)
                if File.Exists p then
                    copyFile p f
            )

            refFiles |> List.iter (fun f ->
                let p = Path.Combine(refAsmDir, f)
                if File.Exists p then
                    copyFile p f
            )

            // Build command line arguments & start FSI session
            let argv = [| @"fsiAnyCpu.exe" |]
            let allArgs = Array.append argv [|"--noninteractive" |]


            let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
            let result =
                try
                    FsiEvaluationSession.Create(fsiConfig, allArgs, inStream, outStream, errStream)  
                with e ->
                    Log.warn "%A" e
                    Unchecked.defaultof<FsiEvaluationSession>

            getError() |> ignore

            result

        let getSession() =
            lock sync (fun () ->
                match fsiSession with
                    | Some s -> s
                    | None ->
                        let s = initSession()
                        fsiSession <- Some s
                        s
            )

        
        member x.Completions(code : string, ?line : int, ?col : int) =
            try
                let s = getSession()
                let lines = code.Split([|"\r\n"|], StringSplitOptions.None)

                let parse, check, _  = s.ParseAndCheckInteraction(code)

                let line = defaultArg line lines.Length
                let col = defaultArg col lines.[line - 1].Length

                let decls = 
                    check.GetDeclarationListInfo
                        (Some parse, line, col, lines.[line - 1], [], "", fun _ -> false)
                    |> Async.RunSynchronously

                decls.Items |> Array.map (fun i -> 
                    let t = i.DescriptionText

                    let docToString (xml : FSharpXmlDoc) =
                        match xml with
                            | FSharpXmlDoc.Text(str) -> str
                            //| FSharpXmlDoc.XmlDocFileSignature(file, key) -> sprintf "%s/%s" file key
                            | _ -> ""

                    let description = 
                        match t with
                            | FSharpToolTipText elements ->
                                elements |> List.collect (fun str -> 
                                    match str with
                                        | FSharpToolTipElement.Single(str,doc) -> [str,docToString doc]
                                        | FSharpToolTipElement.Group l -> l |> List.map (fun (str, doc) -> str, docToString doc)
                                        | _ -> []
                                )

                    { Glyph = i.Glyph; Name = i.Name; Description = description }
                ) |> Array.toList
            with e ->
                printfn "%A" e
                []


//        member x.ToolTip(code : string, line : int, col : int) =
//            let s = getSession()
//            let lines = code.Split([|'\n'|], StringSplitOptions.None)
//
//            let parse, check, _  = s.ParseAndCheckInteraction(code)
//
//            let symUse = check.GetSymbolUseAtLocation(line, col, lines.[line - 1], []) |> Async.RunSynchronously
//            match symUse with
//                | Some sym ->
//                    let identToken = Parser.tagOfToken(Parser.token.IDENT("")) 
//
//                    let res = check.GetToolTipTextAlternate(line, col, lines.[line - 1], [sym.Symbol.DisplayName], identToken) |> Async.RunSynchronously
//
//                    res
//                | None ->
//                    FSharpToolTipText []

        member x.Handle = getSession()

        member x.Dispose() =
            inStream.Dispose()
            sbOut.Dispose()
            sbErr.Dispose()
            outStream.Dispose()
            errStream.Dispose()
            getSession().Interrupt()

        member x.evaluate text =
            try
                match getSession().EvalExpression(text) with
                    | Some value -> FsiSuccess value.ReflectionValue
                    | None -> FsiSuccess (() :> obj)
            with e ->
                getError() |> FsiError

        member x.execute text =
            try
                getSession().EvalInteraction(text)
                FsiSuccess ()
            with e ->
                getError() |> FsiError

        member x.addReference (path : string) =
            //Log.line "added reference to %A" (Path.GetFileName path)
            let esc = path.Replace("\\", "\\\\")
            x.execute ("#r \"" + esc + "\"") |> ignore
            getError() |> ignore

        member x.compileUntyped (opened : list<string>) (code : string) : FsiResult<obj> =
            let lineOffset = opened.Length + 3
            let colOffset = 8

            let opened = opened |> List.map (sprintf "    open %s") |> String.concat "\r\n"

            let code = String.indent 2 code

            let nsId = Interlocked.Increment(&currentNamespaceId)
            let ns = sprintf "CodeDom%d" nsId
            let c = sprintf "module %s =\r\n%s\r\n    let run() =\r\n        ()\r\n%s\r\n" ns opened code

        

            try
                getSession().EvalInteraction(c)
                match x.evaluate (sprintf "%s.run()" ns) with
                    | FsiSuccess a -> FsiSuccess a
                    | FsiError e ->
                        let errors = e.errors |> List.map (fun e -> { e with line = e.line - lineOffset; col = e.col - colOffset })
                        { errors = errors} |> FsiError
            with e ->
                let errors = getError().errors
                let errors = errors |> List.map (fun e -> { e with line = e.line - lineOffset; col = e.col - colOffset })
                { errors = errors} |> FsiError

        member x.compile (opened : list<string>) (code : string) : 'a=
            match x.compileUntyped opened code with
                | FsiSuccess o -> o |> unbox<'a>
                | FsiError e -> failwith (sprintf "%A" e)

        member x.compileModule (code : string) : FsiResult<Type> =
            let lineOffset = 0
            let colOffset = 1
            try

                let rx = System.Text.RegularExpressions.Regex "module[ \t\r\n]+(?<name>.+)[ \t\r\n]+="

                let m = rx.Match code
                if m.Success then
                    let session = getSession()
                    let moduleName = m.Groups.["name"].Value

                    session.EvalInteraction(code)

                    let getAss = "System.Reflection.Assembly.GetExecutingAssembly()"
                
                    match session.EvalExpression(getAss) with
                        | Some value ->
                            let ass = value.ReflectionValue |> unbox<System.Reflection.Assembly>
                            match ass.GetTypes() |> Array.rev |> Array.tryPick (fun t -> let t = t.GetNestedType(moduleName) in if t <> null then Some t else None) with
                                | Some t -> FsiSuccess t
                                | None -> FsiSuccess null
                        | _ -> 
                            FsiSuccess null

                
                else
                    FsiSuccess null

            with e ->
                let errors = getError().errors
                let errors = errors |> List.map (fun e -> { e with line = e.line - lineOffset; col = e.col - colOffset })
                { errors = errors} |> FsiError

        member x.run (opened : list<string>) (code : string) =
            x.compile opened code
