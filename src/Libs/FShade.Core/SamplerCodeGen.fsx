﻿#r @"..\..\..\Bin\Debug\Aardvark.Base.dll"
#r @"..\..\..\Bin\Debug\Aardvark.Base.TypeProviders.dll"
#r @"..\..\..\Bin\Debug\Aardvark.Base.FSharp.dll"
#r @"..\..\..\Bin\Debug\FShade.Imperative.dll"
#r @"..\..\..\Bin\Debug\FShade.Core.dll"

open System
open System.IO
open FShade
open Aardvark.Base
open System.Runtime.CompilerServices


let namespaceName = "FShade"

let types = [ SamplerType.Float; SamplerType.Int ]
let dims = [ SamplerDimension.Sampler1d; SamplerDimension.Sampler2d; SamplerDimension.Sampler3d; SamplerDimension.SamplerCube]
let arr = [ true; false ]
let ms = [ true; false ]
let shadow = [ true; false ]


let allImageCombinations =
    [
        for t in types do
            for d in dims do
                let arr = if d = SamplerDimension.Sampler3d then [false] else arr
                for a in arr do
                    for m in ms do
                        yield (t,d,a,m)
    ]

let allCombinations =
    [
        for t in types do
            for d in dims do
                let arr = if d = SamplerDimension.Sampler3d then [false] else arr
                for a in arr do
                    for m in ms do
                        for s in shadow do
                            if t = SamplerType.Int && s then 
                                ()
                            else
                                yield (t,d,a,m,s)
    ]

let formats =
    [
        "RGBA"
        "RGB"
        "RG"
        "R"
    ]

let mutable indent = ""

let builder = System.Text.StringBuilder()

let line fmt = Printf.kprintf (fun str -> Console.WriteLine("{0}{1}", indent, str); builder.AppendLine(indent + str) |> ignore) fmt
let start fmt = Printf.kprintf (fun str -> Console.WriteLine("{0}{1}", indent, str); builder.AppendLine(indent + str) |> ignore; indent <- indent + "    ") fmt
let stop() = Console.WriteLine(""); builder.AppendLine("") |> ignore; indent <- indent.Substring 4



type SampleVariants =
    | None = 0x0
    | Bias = 0x1
    

let samplerFunction (comment : string) (variants : SampleVariants) (name : string) (args : list<string * string>) (ret : string) =
    let args = args |> List.map (fun (n,t) -> sprintf "%s : %s" n t) |> String.concat ", "
    line "/// %s" comment
    line "member x.%s(%s) : %s = onlyInShaderCode \"%s\"" name args ret name
    line ""

    if (variants &&& SampleVariants.Bias) <> SampleVariants.None then
        line "/// %s with lod-bias" comment
        line "member x.%s(%s, lodBias : float) : %s = onlyInShaderCode \"%s\"" name args ret name
        line ""


let floatVec (c : int) =
    match c with
        | 1 -> "float"
        | n -> sprintf "V%dd" n

let intVec (c : int) =
    match c with
        | 1 -> "int"
        | n -> sprintf "V%di" n

let run() =
    builder.Clear() |> ignore

    line "namespace FShade"
    line "open Aardvark.Base"
    line ""
    line ""

    for (t,d,a,m,s) in allCombinations do
        let prefix =
            match t with
                | SamplerType.Float -> ""
                | SamplerType.Int -> "Int"
                | _ -> ""

        let dim =
            match d with
                | SamplerDimension.Sampler1d -> "1d"
                | SamplerDimension.Sampler2d -> "2d"
                | SamplerDimension.Sampler3d -> "3d"
                | SamplerDimension.SamplerCube -> "Cube"
                | _ -> "2d"

        let ms = if m then "MS" else ""
        let arr = if a then "Array" else ""
        let shadow = if s then "Shadow" else ""


        let name = sprintf "%sSampler%s%s%s%s" prefix dim arr shadow ms

        let returnType =
            match t with
                | SamplerType.Float -> 
                    if s then "float"
                    else "V4d"
                | SamplerType.Int -> "V4i"
                | _ -> failwith "unknown sampler baseType"

        let coordComponents =
            match d with
                | SamplerDimension.Sampler1d -> 1
                | SamplerDimension.Sampler2d -> 2
                | SamplerDimension.Sampler3d -> 3
                | SamplerDimension.SamplerCube -> 3
                | _ -> failwith "unsupported sampler-kind"

//        let coordComponents =
//            if a then coordComponents + 1
//            else coordComponents
//
//        let coordComponents =
//            if s then coordComponents + 1
//            else coordComponents

        let coordType = floatVec coordComponents
        let projCoordType = floatVec (coordComponents + 1)
        let texelCoordType = intVec coordComponents

        let sizeType =
            match d with
                | SamplerDimension.Sampler1d -> "int"
                | SamplerDimension.Sampler2d -> "V2i"
                | SamplerDimension.Sampler3d -> "V3i"
                | SamplerDimension.SamplerCube -> "V2i"
                | _ -> failwith "unsupported sampler-kind"


        start "type %s(tex : ISemanticValue, state : SamplerState) =" name

        start "interface ISampler with"
        line  "member x.Texture = tex"
        line  "member x.State = state"
        stop  ()

        line  "static member Dimension = SamplerDimension.Sampler%s" dim
        line  "static member ValueType = typeof<%s>" returnType
        line  "static member CoordType = typeof<%s>" coordType
        line  "static member IsArray = %s" (if a then "true" else "false")
        line  "static member IsShadow = %s" (if s then "true" else "false")
        line  "static member IsMultisampled = %s" (if m then "true" else "false")
        line  ""


        line  "/// the mipmap-levels for the sampler"
        line  "member x.MipMapLevels : int = onlyInShaderCode \"MipMapLevels\""
        line  ""

        line  "/// the size for the sampler"
        line  "member x.GetSize (level : int) : %s = onlyInShaderCode \"GetSize\"" sizeType
        line  ""

        line  "/// the size for the sampler"
        line  "member x.Size : %s = onlyInShaderCode \"Size\"" sizeType
        line  ""


        let additionalArgs =
            match a, s with
                | true, true -> ["slice", "int"; "cmp", returnType]
                | true, false -> ["slice", "int"]
                | false, true -> ["cmp", returnType]
                | false, false -> []

        if not m then
            samplerFunction 
                "regular sampled texture-lookup"
                SampleVariants.Bias
                "Sample"
                (["coord", coordType] @ additionalArgs)
                returnType

        // Cubemap, multisample, and buffer samplers are not allowed
        if d <> SamplerDimension.SamplerCube && not m then
            samplerFunction 
                "regular sampled texture-lookup with offset"
                SampleVariants.Bias
                "SampleOffset"
                (["coord", coordType] @ additionalArgs @ ["offset", texelCoordType])
                returnType

        // Array, cubemap, multisample, and buffer samplers cannot be used with this function
        if d <> SamplerDimension.SamplerCube && not m && not a then
            samplerFunction 
                "projective sampled texture-lookup"
                SampleVariants.Bias
                "SampleProj"
                (["coord", projCoordType] @ additionalArgs)
                returnType

        if not m && not (a && s && d = SamplerDimension.SamplerCube) then
            samplerFunction 
                "sampled texture-lookup with given level"
                SampleVariants.None
                "SampleLevel"
                (["coord", coordType] @ additionalArgs @ ["level", "float"])
                returnType

        // This function works for sampler types that are not multisample, buffer texture, or cubemap array samplers
        if d <> SamplerDimension.SamplerCube && not m then
            samplerFunction 
                "sampled texture-lookup with explicit gradients"
                SampleVariants.None
                "SampleGrad"
                (["coord", coordType] @ additionalArgs @ ["dTdx", coordType; "dTdy", coordType])
                returnType

        if not m then
            samplerFunction
                "query lod levels"
                SampleVariants.None
                "QueryLod"
                ["coord", coordType]
                "V2d"


        if d = SamplerDimension.Sampler2d && not m then
            let additionalArgs = 
                if a then ["slice", "int"]
                else []

            let gatherType =
                match t with
                    | SamplerType.Int -> "V4i"
                    | _ -> "V4d"

            samplerFunction 
                "gathers one component for the neighbouring 4 texels"
                SampleVariants.None
                "Gather"
                (["coord", coordType] @ additionalArgs @ ["comp", "int"])
                gatherType

            samplerFunction 
                "gathers one component for the neighbouring 4 texels with an offset"
                SampleVariants.None
                "GatherOffset"
                (["coord", coordType] @ additionalArgs @ ["offset", texelCoordType; "comp", "int"])
                gatherType



        if d <> SamplerDimension.SamplerCube then
            if m then
                samplerFunction 
                    "non-sampled texture read"
                    SampleVariants.None
                    "Read"
                    (["coord", texelCoordType; "sample", "int"])
                    returnType
            else
                samplerFunction 
                    "non-sampled texture read"
                    SampleVariants.None
                    "Read"
                    (["coord", texelCoordType; "lod", "int"])
                    returnType


            if not s && not a && not m then
                line "member x.Item"
                line "    with get (coord : %s) : %s = onlyInShaderCode \"Fetch\"" texelCoordType returnType 
                line ""
                line "member x.Item"
                line "    with get(coord : %s, level : int) : %s = onlyInShaderCode \"Fetch\"" texelCoordType returnType 
                
//                if coordComponents > 1 then
//                    let argNames = ["cx"; "cy"; "cz"; "cw"]
//                    let args = argNames |> List.take coordComponents |> List.map (sprintf "%s : int") |> String.concat ", "
//                    line  "member x.Item"
//                    line  "    with get (%s) : %s = onlyInShaderCode \"Fetch\"" args returnType 
//                    line  ""
        
        stop()
        //line ""


    line  "[<AutoOpen>]"
    start "module SamplerBuilders = "

    for (t,d,a,m,s) in allCombinations do
        let prefix =
            match t with
                | SamplerType.Float -> ""
                | SamplerType.Int -> "Int"
                | _ -> ""

        let dim =
            match d with
                | SamplerDimension.Sampler1d -> "1d"
                | SamplerDimension.Sampler2d -> "2d"
                | SamplerDimension.Sampler3d -> "3d"
                | SamplerDimension.SamplerCube -> "Cube"
                | _ -> "2d"

        let ms = if m then "MS" else ""
        let arr = if a then "Array" else ""
        let shadow = if s then "Shadow" else ""


        let typeName = sprintf "%sSampler%s%s%s%s" prefix dim arr shadow ms
        let builderName = sprintf "%sBuilder" typeName
        let valueName = 
            match t with
                | SamplerType.Float -> sprintf "sampler%s%s%s%s" dim arr shadow ms
                | _ -> sprintf "intSampler%s%s%s%s" dim arr shadow ms

        start "type %s() = " builderName
        line  "inherit SamplerBaseBuilder()"
        line  "member x.Run((t : ShaderTextureHandle, s : SamplerState)) ="
        line  "    %s(t, s)" typeName
        line  "member x.Run(((t : ShaderTextureHandle, count : int), s : SamplerState)) ="
        line  "    Array.init count (fun i -> %s(t.WithIndex(i), s))" typeName
        stop  ()
        line  "let %s = %s()" valueName builderName
        line  ""

    stop ()


    for (t,d,a,m) in allImageCombinations do
        let prefix =
            match t with
                | SamplerType.Float -> ""
                | SamplerType.Int -> "Int"
                | _ -> ""

        let dim =
            match d with
                | SamplerDimension.Sampler1d -> "1d"
                | SamplerDimension.Sampler2d -> "2d"
                | SamplerDimension.Sampler3d -> "3d"
                | SamplerDimension.SamplerCube -> "Cube"
                | _ -> "2d"

        let ms = if m then "MS" else ""
        let arr = if a then "Array" else ""


        let name = sprintf "%sImage%s%s%s" prefix dim arr ms 

        let returnType =
            match t with
                | SamplerType.Float -> "V4d"
                | SamplerType.Int -> "V4i"
                | _ -> failwith "unknown image baseType"

        let coordComponents =
            match d with
                | SamplerDimension.Sampler1d -> 1
                | SamplerDimension.Sampler2d -> 2
                | SamplerDimension.Sampler3d -> 3
                | SamplerDimension.SamplerCube -> 3
                | _ -> failwith "unsupported image-kind"

        let coordType = intVec coordComponents

        let sizeType =
            match d with
                | SamplerDimension.Sampler1d -> "int"
                | SamplerDimension.Sampler2d -> "V2i"
                | SamplerDimension.Sampler3d -> "V3i"
                | SamplerDimension.SamplerCube -> "V2i"
                | _ -> failwith "unsupported sampler-kind"

        let iface =
            match t with
                | SamplerType.Float -> "Formats.IFloatingFormat"
                | SamplerType.Int -> "Formats.ISignedFormat"
                | _ -> ""

        start "type %s<'f when 'f :> %s>() =" name iface
        
        line "interface IImage"
        
        line  "static member FormatType = typeof<'f>"
        line  "static member Dimension = SamplerDimension.Sampler%s" dim
        line  "static member ValueType = typeof<%s>" returnType
        line  "static member CoordType = typeof<%s>" coordType
        line  "static member IsArray = %s" (if a then "true" else "false")
        line  "static member IsMultisampled = %s" (if m then "true" else "false")
        line  ""

        line "member x.Size : %s = onlyInShaderCode \"Size\"" sizeType


        let args =
            [
                yield "coord", coordType
                if a then yield "slice", "int"
                if m then yield "sample", "int"
            ]

        let itemArgs = args |> List.map (fun (n,t) -> sprintf "%s : %s" n t) |> String.concat ", "

        start "member x.Item"
        line "with get(%s) : %s = onlyInShaderCode \"fetch\"" itemArgs returnType
        line "and set(%s) (v : %s) : unit = onlyInShaderCode \"write\"" itemArgs returnType
        stop()


        if t = SamplerType.Int then
            line "member x.AtomicAdd(%s, data : int) : int = onlyInShaderCode \"AtomicAdd\"" itemArgs
            line "member x.AtomicMin(%s, data : int) : int = onlyInShaderCode \"AtomicMin\"" itemArgs
            line "member x.AtomicMax(%s, data : int) : int = onlyInShaderCode \"AtomicMax\"" itemArgs
            line "member x.AtomicAnd(%s, data : int) : int = onlyInShaderCode \"AtomicAnd\"" itemArgs
            line "member x.AtomicOr(%s, data : int) : int = onlyInShaderCode \"AtomicOr\"" itemArgs
            line "member x.AtomicXor(%s, data : int) : int = onlyInShaderCode \"AtomicXor\"" itemArgs
            line "member x.AtomicExchange(%s, data : int) : int = onlyInShaderCode \"AtomicExchange\"" itemArgs
            line "member x.AtomicCompareExchange(%s, cmp : int, data : int) : int = onlyInShaderCode \"AtomicCompareExchange\"" itemArgs


        stop()
        ()


    let str = builder.ToString()
    let fileName = Path.Combine(__SOURCE_DIRECTORY__, "Samplers.fs")
    File.WriteAllText(fileName, str)

    ()

