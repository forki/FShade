﻿namespace FShade.Compiler

open System
open System.Reflection
open Microsoft.FSharp.Quotations
open Aardvark.Base
open FShade.Utils


[<AutoOpen>]
module StateHelpers =

    /// <summary>
    /// Utility for "raising" an error inside a compile-expression
    /// </summary>
    let error fmt = Printf.ksprintf(fun str -> { runCompile = fun s -> Error str }) fmt

    /// <summary>
    /// the empty compiler-state for a given compiler. Note that InitialState
    /// is used internally to create a initial userState.
    /// </summary>
    let emptyCompilerState (c : ICompiler<_>) = 
        let user = c.InitialState()
        { compiler = c
          types = PersistentHashSet.empty
          functions = PersistentHashSet.empty
          constantId = 0
          constants = HashMap.empty
          
          lambdaId = 0
          lambdas = Map.empty
          
          defines = Map.empty
          functionId = 0
          
          uniformId = 0
          uniforms = HashMap.empty
          
          bound = Set.empty
          userState = user 
          initialUserState = user }


/// <summary>
/// Definition of the compile-monad and some utilities allowing to map/filter/fold
/// sequences and lists using the compile-monad.
/// </summary>
[<AutoOpen>]
module CompilerMonad = 

    type CompilerBuilder() =

        member x.Bind(m : Compiled<'a, 's>, f : 'a -> Compiled<'b, 's>) : Compiled<'b, 's> =
            { runCompile = fun s -> 
                match m.runCompile s with
                    | Success(s,v) -> (f v).runCompile s
                    | Error e -> Error e }

        member x.For(elements : #seq<'a>, f : 'a -> Compiled<unit, 's>) : Compiled<unit, 's> =
            { runCompile = fun s ->
                elements |> Seq.fold (fun s e -> 
                                match s with
                                    | Success (s, ()) -> (f e).runCompile s 
                                    | Error e -> Error e
                            ) (Success(s, ())) }

        member x.Delay(f : unit -> Compiled<'a, 's>) = 
            { runCompile = fun s -> (f ()).runCompile s }

        member x.Return(v : 'a) : Compiled<'a, 's> =
            { runCompile = fun s ->
                Success(s,v) }
                                    
        member x.ReturnFrom(m : Compiled<'a, 's>) = 
            m

        member x.Run(c : Compiled<'a, 's>) =
            { runCompile = fun s ->
                match c.runCompile s with
                    | Success (s,v) -> Success(s,v)
                    | Error e -> Error e
            }

    type ErrorMonadBuilder() =
        member x.Bind(e : Error<'a>, f : 'a -> Error<'b>) =
            match e with
                | Success v -> f v
                | Error e -> Error e

        member x.Return(v) = Success v
        member x.ReturnFrom(v : Error<'a>) = v
        member x.Delay(f : unit -> Error<'a>) = f
        member x.Run(f : unit -> Error<'a>) = f()
        member x.For(s : seq<'a>, f : 'a -> Error<unit>) =
            let rec acc (l : list<'a>) =
                match l with
                    | [] -> Success ()
                    | v::rest ->
                        match f v with
                            | Success () -> acc rest
                            | Error e -> Error e

            s |> Seq.toList |> acc

        member x.While(guard : unit -> bool, f : unit -> Error<unit>) =
            if guard() then
                match f() with
                    | Success () ->
                        x.While(guard, f)
                    | Error e ->
                        Error e
            else
                Success ()

        member x.Zero() = Success()

        member x.Combine (l : Error<unit>, r : unit -> Error<'a>) =
            match l with
                | Success () -> r()
                | Error e -> Error e

    let compile = CompilerBuilder()
    

    let runCompile (c : ICompiler<'s>) (v : Compiled<'a,'s>) =
        match v.runCompile (emptyCompilerState c) with
            | Success(_,v) -> Success v
            | Error e -> Error e

    let readFrom (v : Error<'a>) =
        { runCompile = fun s ->
            match v with    
                | Success v -> Success(s,v)
                | Error e -> Error e
        }

    module Seq = 
        let rec mapCi (f : int -> 'a -> Compiled<'b,'s>) (elements : seq<'a>) : Compiled<seq<'b>,'s> =
            { runCompile = fun s -> 
                    let mutable c = s
                    let mutable error = None

                    let l = System.Collections.Generic.List<'b>()
                    let mutable index = 0
                    for e in elements do
                        match error with
                            | None -> match (f index e).runCompile c with
                                        | Success(s,v) -> l.Add(v)
                                                          c <- s
                                        | Error e -> error <- Some e
                            | _ -> ()
                        index <- index + 1

                    match error with
                        | None -> Success(c,l :> seq<'b>)
                        | Some e -> Error e }

        let rec mapC (f : 'a -> Compiled<'b,'s>) (elements : seq<'a>) : Compiled<seq<'b>,'s> =
            mapCi (fun _ e -> f e) elements

        let rec mapCs (seed : 'sr) (f : 'sr -> 'a -> Compiled<'sr * 'b,'s>) (elements : seq<'a>) : Compiled<seq<'b>,'s> =
            { runCompile = fun s -> 
                    let mutable c = s
                    let mutable error = None

                    let l = System.Collections.Generic.List<'b>()
                    let mutable state = seed
                    
                    for e in elements do
                        match error with
                            | None -> match (f state e).runCompile c with
                                        | Success(s,(newState, v)) -> 
                                            l.Add(v)
                                            c <- s
                                            state <- newState
                                        | Error e -> error <- Some e
                            | _ -> ()

                    match error with
                        | None -> Success(c,l :> seq<'b>)
                        | Some e -> Error e }


        let rec collectC (f : 'a -> Compiled<#seq<'b>, 's>) (elements : seq<'a>) : Compiled<seq<'b>,'s> =
            compile {
                let! ll = mapC f elements
                return Seq.concat ll
            }      

    module List =
        let rec filterC (f : 'a -> Compiled<bool, 's>) (elements : list<'a>) : Compiled<list<'a>,'s> =
            { runCompile = fun s -> 
                    let mutable c = s
                    let mutable error = None
                    let mutable index = 0
                    let l = System.Collections.Generic.List<'a>()
                    for e in elements do
                        match error with
                            | None -> match (f e).runCompile c with
                                        | Success(s,v) -> match v with
                                                            | true -> l.Add(e)
                                                            | _ -> ()
                                                          c <- s
                                        | Error e -> error <- Some e
                            | _ -> ()
                        index <- index + 1

                    match error with
                        | None -> Success(c,l |> Seq.toList)
                        | Some e -> Error e }

        let rec mapCi (f : int -> 'a -> Compiled<'b,'s>) (elements : list<'a>) : Compiled<list<'b>,'s> =
            { runCompile = fun s -> 
                    let mutable c = s
                    let mutable error = None
                    let mutable index = 0
                    let l = System.Collections.Generic.List<'b>()
                    for e in elements do
                        match error with
                            | None -> match (f index e).runCompile c with
                                        | Success(s,v) -> l.Add(v)
                                                          c <- s
                                        | Error e -> error <- Some e
                            | _ -> ()
                        index <- index + 1

                    match error with
                        | None -> Success(c,l |> Seq.toList)
                        | Some e -> Error e }

        let rec mapC (f : 'a -> Compiled<'b,'s>) (elements : list<'a>) : Compiled<list<'b>,'s> =
            mapCi (fun _ e -> f e) elements

        let rec collectC (f : 'a -> Compiled<list<'b>, 's>) (elements : list<'a>) : Compiled<list<'b>,'s> =
            compile {
                let! ll = mapC f elements
                return List.concat ll
            }


/// <summary>
/// Contains various functions for reading/writing the compile-monad-state.
/// </summary>
[<AutoOpen>]
module StateModification = 
    let usedTypes = { runCompile = fun s -> Success(s, s.types) }
    let usedConstants = { runCompile = fun s -> Success(s, s.constants) }
    let usedDefines = { runCompile = fun s -> Success(s, s.defines) }
    let usedFunctions = { runCompile = fun s -> Success(s, s.functions) }

    let putUsedTypes t = { runCompile = fun s -> Success({ s with types = t }, ()) }
    let putUsedFunctions f = { runCompile = fun s -> Success({ s with functions = f }, ()) }

    let compilerState = { runCompile = fun s -> Success(s,s.userState) }
    let putCompilerState s = { runCompile = fun o -> Success({ o with userState = s },()) }
    let modifyCompilerState f = { runCompile = fun o -> Success({ o with userState = f o.userState },()) }
    let resetCompilerState = 
        { runCompile = fun o -> 
            if isNull (o.compiler :> obj) then Success ({ o with userState = o.initialUserState}, ())
            else Success({ o with userState = o.compiler.ResetState(o.userState) },()) 
        }

    let addUsedType t = { runCompile = fun o -> Success({ o with  types = PersistentHashSet.add t o.types },()) }
    let addMethod mi = { runCompile = fun o -> Success({ o with functions = PersistentHashSet.add (MethodFunction mi) o.functions },()) }
    let addFunction args body = { runCompile = fun o -> Success({ o with functions = PersistentHashSet.add (SpecialFunction(o.functionId, args, body)) o.functions; functionId = o.functionId + 1},o.functionId) }

    let getUserUniform t obj = 
        { runCompile = 
            fun o -> 
                match HashMap.tryFind obj o.uniforms with
                    | Some v -> Success(o, v)
                    | None ->
                        let v = Var(sprintf "userUniform%d" o.uniformId, t)
                        let newState =
                            { o with 
                                uniforms = HashMap.add obj v o.uniforms
                                uniformId = o.uniformId + 1
                            }

                        Success(newState, v)
        }



    let addBound v = { runCompile = fun o -> Success({o with bound = Set.add v o.bound}, ()) }
    let removeBound v = { runCompile = fun o -> Success({o with bound = Set.remove v o.bound}, ()) }
    let isBound v = { runCompile = fun o -> Success(o, Set.contains v o.bound) }

    
    let lambdas = { runCompile = fun o -> Success(o, o.lambdas |> Map.toSeq |> Seq.map (fun (_,(e,id)) -> (e, id)) |> HashMap.ofSeq) }
    let resetLambdas() = { runCompile = fun o -> Success({ o with  lambdas = Map.empty; lambdaId = 0 }, ()) }

    let addLambda (lambda : Expr) = { runCompile = fun o -> 
 
        let str = lambda.ToString()
        match Map.tryFind str o.lambdas with
            | Some (_,id) -> Success(o, id)
            | _ -> Success({ o with  lambdas = Map.add str (lambda, o.lambdaId) o.lambdas; lambdaId = o.lambdaId + 1}, o.lambdaId) }

    let asConstant e =
        { runCompile = fun o ->
            match HashMap.tryFind e o.constants with
                | Some e -> Success(o, Expr.Var(e))
                | None ->
                    let v = Var(sprintf "constant%d" o.constantId, e.GetType())
                    let newState = { o with  constantId = o.constantId + 1; constants = HashMap.add e v o.constants }
                    Success(newState,Expr.Var(v)) }
     
    let asDefine (name : string) (value : string) =
        { runCompile = fun o ->
            match Map.containsKey name o.defines with
                | true -> Success(o, name)
                | _ -> let newState = { o with defines = Map.add name value o.defines }
                       Success(newState, name) }
