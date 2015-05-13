﻿// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.
open System
open Aardvark.Base
open FShade
open FShade.Demo


module Simple =
    open Aardvark.Base
    open FShade
    
    type UniformScope with
        member x.CameraLocation : V3d = x?PerView?CameraLocation
        member x.LightLocation : V3d = x?PerLight?LightLocation

        member x.ModelTrafo : M44d = x?PerModel?ModelTrafo
        member x.ViewProjTrafo : M44d = x?PerView?ViewProjTrafo

    type V = { [<Semantic("Positions")>] p : V4d 
               [<Semantic("World")>] wp : V4d
               [<Semantic("Normals")>] n : V3d
               [<Semantic("Tangents")>] t : V3d
               [<Semantic("BiNormals")>] b : V3d
               [<Semantic("TexCoords")>] tc : V2d
               [<Semantic("Colors")>] color : V4d }

    let DiffuseColorTexture =
           sampler2d {
               texture uniform?DiffuseTexture
        }
                
    let NormalMap =
           sampler2d {
               texture uniform?NormalMap
        }

    let trafo(v : V) =
        vertex {
            let world = uniform.ModelTrafo * v.p
            return { v with p = uniform.ViewProjTrafo * world; wp = world }
        }

    let normals(v : V) =
        fragment {
            return V4d(0.5 * (v.n.Normalized + V3d.III), 1.0)
        }
        
    let bump (v : V) =
        fragment {
            let s = 2.0 * NormalMap.Sample(v.tc).XYZ - V3d.III
            let n = s.X * v.t + s.Y * v.b + s.Z * v.n
            return { v with n = n.Normalized }
        }
        
    let white (v : V) =
        fragment {
            return V4d.IIII
        }
            
    let texture (v : V) =
           fragment {
               return DiffuseColorTexture.Sample(v.tc)
        }
            
    let pointSurface (size : V2d) (p : Point<V>) =
        let sx = size.X
        let sy = size.Y
        triangle {
            let pos = p.Value.p 
            let pxyz = pos.XYZ / pos.W
            //let v = p.Value

            let p00 = V3d(pxyz + V3d( -sx, -sy, 0.0 ))
            let p01 = V3d(pxyz + V3d( -sx,  sy, 0.0 ))
            let p10 = V3d(pxyz + V3d(  sx, -sy, 0.0 ))
            let p11 = V3d(pxyz + V3d(  sx,  sy, 0.0 ))

            yield { p.Value with p = V4d(p00 * pos.W, pos.W); tc = V2d.OO }
            yield { p.Value with p = V4d(p10 * pos.W, pos.W); tc = V2d.IO }
            yield { p.Value with p = V4d(p01 * pos.W, pos.W); tc = V2d.OI }
            yield { p.Value with p = V4d(p11 * pos.W, pos.W); tc = V2d.II }

        }
            
    let light (v : V) =
        fragment {
            let n = v.n.Normalized

            let c = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
            let l = uniform.LightLocation - v.wp.XYZ |> Vec.normalize
            let r = -Vec.reflect c n |> Vec.normalize

            let d = Vec.dot l n |> clamp 0.0 1.0
            let s = Vec.dot r l |> clamp 0.0 1.0

            return  v.color.XYZ * (0.2 + 0.8 * d) + V3d.III * pow s 64.0
        }    
            
            
            


[<EntryPoint>]
let main argv = 

    let effect = [Shaders.simpleTrafoShader |> toEffect
                  Shaders.textureShader |> toEffect] |> compose


    let effect = [Simple.trafo |> toEffect
                  Simple.pointSurface (V2d(0.06, 0.08)) |> toEffect
                  Simple.white |> toEffect
                  Simple.texture |> toEffect ] |> compose

//    match GLSL.compileEffect effect with
//        | Success (uniforms, code) ->
//            for (k,v) in uniforms |> Map.toSeq do
//                if v.IsSamplerUniform then
//                    let sem,sam = v.Value |> unbox<string * SamplerState>
//                    printfn "%s -> %s:\r\n%A" sem k sam
//
//            printfn "%s" code
//        | Error e ->
//            printfn "ERROR: %A" e
//

    //FShade.Resources.Icon |> printfn "%A"

    let w = new Window()

    let ps = [|V3f.OOO; V3f.IOO; V3f.IIO; V3f.OIO|] :> Array
    let tc = [|V2f.OO; V2f.IO; V2f.II; V2f.OI|] :> Array
    let n = [|V3f.OOI; V3f.OOI; V3f.OOI; V3f.OOI|] :> Array
    let b = [|V3f.IOO; V3f.IOO; V3f.IOO; V3f.IOO|] :> Array
    let t = [|V3f.OIO; V3f.OIO; V3f.OIO; V3f.OIO|] :> Array
    let indices = [|0;1;2; 0;2;3|] :> Array

    let sg = Sg.geometry (Some indices) (["Positions", ps; "TexCoords", tc; "Normals", n; "BiNormals", b; "Tangents", t] |> Map.ofList)
    let sg = Sg.shader "Main" effect sg


    FShade.Debug.EffectEditor.runTray()

    let sg = Sg.fileTexture "DiffuseTexture" @"C:\Users\haaser\Development\WorkDirectory\Server\pattern.jpg" sg
    let sg = Sg.fileTexture "NormalMap"      @"C:\Users\haaser\Development\WorkDirectory\Server\bump.jpg" sg

    //let sg = Sg.uniform "CameraLocation" V3d.III sg


    w.Scene <- sg

    w.Run()

    0
