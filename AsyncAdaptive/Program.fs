open System.Threading
open System.Threading.Tasks
open FSharp.Data.Adaptive

type internal RefCountingTaskCreator<'a>(create : CancellationToken -> Task<'a>) =
    
    let mutable refCount = 0
    let mutable cache : option<Task<'a>> = None
    let mutable cancel : CancellationTokenSource = null
    
    member private x.RemoveRef() =
        lock x (fun () ->
            if refCount = 1 then
                refCount <- 0
                cancel.Cancel()
                cancel.Dispose()
                cancel <- null
                cache <- None
            else
                refCount <- refCount - 1
        )

    member x.New() =
        lock x (fun () ->
            match cache with
            | Some cache ->
                refCount <- refCount + 1
                CancelableTask(x.RemoveRef, cache)
            | None ->
                cancel <- new CancellationTokenSource()
                let task = create cancel.Token
                cache <- Some task
                refCount <- refCount + 1
                CancelableTask(x.RemoveRef, task)
        )
    
and CancelableTask<'a>(cancel : unit -> unit, real : Task<'a>) =
    let cts = new CancellationTokenSource()
    
    let output =
        if real.IsCompleted then
            real
        else
            let tcs = new TaskCompletionSource<'a>()
            let s =
                cts.Token.Register(fun () ->
                    tcs.TrySetCanceled() |> ignore
                )
            real.ContinueWith(fun (t : Task<'a>) ->
                s.Dispose()
                if t.IsFaulted then
                    tcs.TrySetException(t.Exception)
                elif t.IsCanceled then
                    tcs.TrySetCanceled()
                else
                    tcs.TrySetResult(t.Result)
            ) |> ignore
            
            tcs.Task
        
    
    member x.Cancel() = 
        cancel()
        cts.Cancel()
        
    member x.Task =
        output
    
type asyncaval<'a> =
    inherit IAdaptiveObject
    abstract GetValue : AdaptiveToken -> CancelableTask<'a>
    
module AsyncAVal =
    
    type ConstantVal<'a>(value : Task<'a>) =
        inherit ConstantObject()
        
        interface asyncaval<'a> with
            member x.GetValue _ = CancelableTask(id, value)
        
    [<AbstractClass>]
    type AbstractVal<'a>() =
        inherit AdaptiveObject()
        abstract Compute : AdaptiveToken -> CancelableTask<'a>
        
        member x.GetValue token =
            x.EvaluateAlways token x.Compute
        
        interface asyncaval<'a> with
            member x.GetValue t = x.GetValue t
    
    let constant (value : 'a) =
        ConstantVal(Task.FromResult value) :> asyncaval<_>
     
    let ofTask (value : Task<'a>) =
        ConstantVal(value) :> asyncaval<_>
     
    let ofCancelableTask (value : CancelableTask<'a>) =
        ConstantVal(value.Task) :> asyncaval<_>
     
    let ofAVal (value : aval<'a>) =
        if value.IsConstant then
            ConstantVal(task { return AVal.force value }) :> asyncaval<_>
        else
            { new AbstractVal<'a>() with
                member x.Compute t =
                    let real =
                        task {
                            return value.GetValue t
                        }
                    CancelableTask(id, real)
            } :> asyncaval<_>

    let map (mapping : 'a -> CancellationToken -> Task<'b>) (input : asyncaval<'a>) =
        let mutable cache : option<RefCountingTaskCreator<'b>> = None
        { new AbstractVal<'b>() with
            member x.Compute t =
                if x.OutOfDate || Option.isNone cache then
                    let ref =
                        RefCountingTaskCreator(fun ct ->
                            let it = input.GetValue t
                            let s = ct.Register(fun () -> it.Cancel())
                            task {
                                try
                                    let! i = it.Task
                                    return! mapping i ct
                                finally
                                    s.Dispose()
                            }    
                        )
                    cache <- Some ref
                    ref.New()
                else
                    cache.Value.New()
        } :> asyncaval<_>

    let map2 (mapping : 'a -> 'b -> CancellationToken -> Task<'c>) (ca : asyncaval<'a>) (cb : asyncaval<'b>) =
        let mutable cache : option<RefCountingTaskCreator<'c>> = None
        { new AbstractVal<'c>() with
            member x.Compute t =
                if x.OutOfDate || Option.isNone cache then
                    let ref =
                        RefCountingTaskCreator(fun ct ->
                            let ta = ca.GetValue t
                            let tb = cb.GetValue t
                            let s = ct.Register(fun () -> ta.Cancel(); tb.Cancel())
                            task {
                                try
                                    let! va = ta.Task
                                    let! vb = tb.Task
                                    return! mapping va vb ct
                                finally
                                    s.Dispose()
                            }    
                        )
                    cache <- Some ref
                    ref.New()
                else
                    cache.Value.New()
        } :> asyncaval<_>

    // untested!!!!
    let bind (mapping : 'a -> CancellationToken -> asyncaval<'b>) (value : asyncaval<'a>) =
        let mutable cache : option<_> = None
        let mutable innerCache : option<_> = None
        let mutable inputChanged = 0
        let inners : ref<HashSet<asyncaval<'b>>> = ref HashSet.empty
        
        
        
        
        { new AbstractVal<'b>() with
            
            override x.InputChangedObject(_, o) =
                if System.Object.ReferenceEquals(o, value) then
                    inputChanged <- 1
                    lock inners (fun () ->
                        for i in inners.Value do i.Outputs.Remove x |> ignore
                        inners.Value <- HashSet.empty
                    )
            
            member x.Compute t =
                if x.OutOfDate then
                    if Interlocked.Exchange(&inputChanged, 0) = 1 || Option.isNone cache then
                        let outerTask =
                            RefCountingTaskCreator(fun ct ->
                                let it = value.GetValue t
                                let s = ct.Register(fun () -> it.Cancel())
                                task {
                                    try
                                        let! i = it.Task
                                        let inner = mapping i ct
                                        return inner
                                    finally
                                        s.Dispose()
                                }
                            )
                        cache <- Some outerTask
                        
                    let outerTask = cache.Value
                    let ref = 
                        RefCountingTaskCreator(fun ct ->
                            let innerCellTask = outerTask.New()
                            let s = ct.Register(fun () -> innerCellTask.Cancel())
                            task {
                                try
                                    let! inner = innerCellTask.Task
                                    let innerTask = inner.GetValue t
                                    lock inners (fun () -> inners.Value <- HashSet.add inner inners.Value)
                                    let s2 =
                                        ct.Register(fun () ->
                                            innerTask.Cancel()
                                            lock inners (fun () -> inners.Value <- HashSet.remove inner inners.Value)
                                            inner.Outputs.Remove x |> ignore
                                        )
                                    try
                                        let! innerValue = innerTask.Task
                                        return innerValue
                                    finally
                                        s2.Dispose()
                                finally
                                    s.Dispose()
                            }    
                        )
                        
                    innerCache <- Some ref
                        
                    ref.New()
                else
                    innerCache.Value.New()
                    
        } :> asyncaval<_>
        

[<EntryPoint>]
let main argv = 
   
    let l = obj()
    let printfn fmt =
        fmt |> Printf.kprintf (fun str ->
            lock l (fun () ->
                System.Console.WriteLine str
            )
        )
    
    let input = cval 1000
    
    
    let sleeper =
        input |> AsyncAVal.ofAVal |> AsyncAVal.map (fun time ct ->
            printfn "start sleeping"
            task {
                try
                    do! Task.Delay(time, ct)
                    printfn "done sleeping"
                    return time
                with e ->
                    printfn "stop sleeping"
                    return raise e
            }
        )
        
    let a =
        sleeper |> AsyncAVal.map (fun time ct ->
            printfn "start a"
            task {
                try
                    do! Task.Delay(100, ct)
                    printfn "a done"
                    return time
                with e ->
                    printfn "a stopped"
                    return raise e
            }
        )
    let b =
        sleeper |> AsyncAVal.map (fun time ct ->
            printfn "start b"
            task {
                try
                    do! Task.Delay(300, ct)
                    printfn "b done"
                    return time
                with e ->
                    printfn "b stopped"
                    return raise e
            }
        )
        
    let c =
        sleeper |> AsyncAVal.map (fun time ct ->
            printfn "start c"
            task {
                try
                    do! Task.Delay(200, ct)
                    printfn "c done"
                    return time
                with e ->
                    printfn "c stopped"
                    return raise e
            }
        )
        
    let d = (a,b) ||> AsyncAVal.map2 (fun a b _ -> task { return a - b })
        
        
        
    
    sleeper.GetValue(AdaptiveToken.Top).Task.Result |> printfn "result: %A"
    
    a.GetValue(AdaptiveToken.Top).Task.Result |> printfn "a: %A"
    b.GetValue(AdaptiveToken.Top).Task.Result |> printfn "b: %A"
    c.GetValue(AdaptiveToken.Top).Task.Result |> printfn "c: %A"
    d.GetValue(AdaptiveToken.Top).Task.Result |> printfn "d: %A"
    
    
    printfn "change"
    transact (fun () -> input.Value <- 500)
    
    let tc = c.GetValue(AdaptiveToken.Top)
    let td = d.GetValue(AdaptiveToken.Top)
    
    tc.Cancel()
    td.Task.Result |> printfn "d = %A"
    
    
    printfn "eval tasks"
    let va = a.GetValue(AdaptiveToken.Top)
    let vb = b.GetValue(AdaptiveToken.Top)
    
    printfn "cancel a"
    va.Cancel()
    
    printfn "wait b"
    vb.Task.Result |> printfn "b: %A"
    
    printfn "change"
    transact (fun () -> input.Value <- 400)
    
    printfn "eval tasks"
    let va = a.GetValue(AdaptiveToken.Top)
    let vb = b.GetValue(AdaptiveToken.Top)
    
    Thread.Sleep 50
    printfn "cancel a"
    va.Cancel()
    
    printfn "cancel b"
    vb.Cancel()
    
    try va.Task.Wait() with _ -> ()
    try vb.Task.Wait() with _ -> ()
    
    let tc = c.GetValue(AdaptiveToken.Top)
    printfn "c: %A" tc.Task.Result
    
    0
