﻿// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.
open System
open Aardvark.Base
open FShade
open FShade.Demo


let (!!) (r : ref<'a>) = !r

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
            let c : V4d = uniform?PerView?Color
            return c
        }
            
    let texture (v : V) =
           fragment {
               return DiffuseColorTexture.Sample(v.tc)
        }
            
    let pointSurface (size : V2d) (p : Point<V>) =
        let sx = size.X
        let sy = size.Y
        triangle {
            let v = p.Value

            match p.Value with
                | { p = pos; n = n } ->

                    let pxyz = pos.XYZ / pos.W
            
                    let p00 = V3d(pxyz + V3d( -sx, -sy, 0.0 ))
                    let p01 = V3d(pxyz + V3d( -sx,  sy, 0.0 ))
                    let p10 = V3d(pxyz + V3d(  sx, -sy, 0.0 ))
                    let p11 = V3d(pxyz + V3d(  sx,  sy, 0.0 ))

                    yield { p.Value with p = uniform.ViewProjTrafo * V4d(p00 * pos.W, pos.W); tc = V2d.OO }
                    yield { p.Value with p = uniform.ViewProjTrafo * V4d(p10 * pos.W, pos.W); tc = V2d.IO }
                    yield { p.Value with p = uniform.ViewProjTrafo * V4d(p01 * pos.W, pos.W); tc = V2d.OI }
                    yield { p.Value with p = uniform.ViewProjTrafo * V4d(p11 * pos.W, pos.W); tc = V2d.II }

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
 
 
module Dead =
    type BillboardVertex =
        {
            [<Position>] position : V4d
            [<Color>] color : V4d
            [<SemanticAttribute("DiffuseColorCoordinate")>] texCoord : V2d
            [<SemanticAttribute("ViewPosition")>] viewPos : V4d
        }

    type UniformScope with
        member x.ModelViewTrafo : M44d = uniform?PerModel?ModelViewTrafo
        member x.ProjTrafo : M44d = uniform?PerView?ProjTrafo
        member x.UserSelected : bool = uniform?UserSelected

    let BillboardTrafo (v : BillboardVertex) =
        vertex {
            let vp = uniform.ModelViewTrafo * v.position
            let pp = uniform.ProjTrafo * vp
            return {
                position = pp
                texCoord = V2d(0,0)
                color = v.color
                viewPos = vp
            }
        }

    let BillboardGeometry (sizes: V2d) (distanceScalingFactor : ref<float>) (v : Point<BillboardVertex>) =
        triangle {
            let f = !!distanceScalingFactor
            yield { position = V4d(1,1,1,1); color = V4d(1,1,1,1); texCoord = V2d(0, 1); viewPos = V4d(1,1,1,1) }
        }

    let BillboardFragment (color: V4d) (v : BillboardVertex) =
        fragment {
            let c = color
            let s = uniform.UserSelected
            let t = v.texCoord
            let comp = V2d(0.5, 0.5)
            let dist = t - comp
            let len = dist.Length
            if len < 0.5 then
                if s then
                    return c
                else
                    return v.color
            else
                discard()
                return V4d(0,0,0,0)
        } 
           
   
module RefsAsUniforms =
    open Microsoft.FSharp.Quotations
    open Microsoft.FSharp.Quotations.Patterns

    let detectRef (f : Expr) =
        match f with
            | Call(None, mi, [Value(arg,t)]) when mi.Name = "op_BangBang" ->
                Some (UserUniform(t.GetGenericArguments().[0], arg))

            | _ ->
                None
         
            


[<EntryPoint>]
let main argv = 

    let effect = [Shaders.simpleTrafoShader |> toEffect
                  Shaders.textureShader |> toEffect] |> compose


    let effect = [Simple.trafo |> toEffect
                  Simple.pointSurface (V2d(0.06, 0.08)) |> toEffect
                  Simple.white |> toEffect
                  ] |> compose

    uniformDetectors <- [RefsAsUniforms.detectRef]

    let r = ref 1.0
    let effect = [Dead.BillboardTrafo |> toEffect; Dead.BillboardGeometry V2d.II r |> toEffect; Dead.BillboardFragment V4d.IIII |> toEffect] |> compose



    let e0 = toEffect (Dead.BillboardGeometry V2d.II (ref 0.0))
    let e1 = toEffect (Dead.BillboardGeometry V2d.II (ref 1.0))

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


    let config =
        {
            GLSL.languageVersion = Version(1,4,0)
            GLSL.enabledExtensions = Set.ofList ["GL_ARB_separate_shader_objects"; "GL_ARB_shading_language_420pack" ]

            GLSL.createUniformBuffers = true
            GLSL.createGlobalUniforms = false
            GLSL.createBindings = true
            GLSL.createDescriptorSets = true
            GLSL.createInputLocations = true
            GLSL.createRowMajorMatrices = true
            GLSL.createPerStageUniforms = true

            GLSL.flipHandedness = true
            GLSL.depthRange = Range1d(0.0,1.0)
        }

    let res = effect |> GLSL.compileEffect410 (Map.ofList ["Colors", typeof<V4d>; "Depth", typeof<float>])
    match res with
        | Success (_,code) ->
            printfn "%s" code
        | Error e ->
            failwith e

    Environment.Exit 0


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
//
//    let sg = Sg.fileTexture "DiffuseTexture" @"C:\Users\haaser\Development\WorkDirectory\Server\pattern.jpg" sg
//    let sg = Sg.fileTexture "NormalMap"      @"C:\Users\haaser\Development\WorkDirectory\Server\bump.jpg" sg

    //let sg = Sg.uniform "CameraLocation" V3d.III sg


    w.Scene <- sg

    w.Run()

    0
