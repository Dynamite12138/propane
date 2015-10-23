﻿

let RE = Regex.REBuilder(Set.ofList ["A"; "B"; "C"; "D"; "X"; "Y"; "M"; "N"], Set.empty)

[<EntryPoint>]
let main argv = 
    (* let r = RE.concat (RE.loc "A") (RE.concat (RE.loc "X") (RE.concat (RE.loc "N") (RE.concat (RE.loc "Y") (RE.loc "B")) )) *)
    (* let r = RE.star RE.inside *)

    let x = RE.Star RE.Inside
    let r1 = RE.Concat (RE.Concat x (RE.Loc "M")) x
    let r2 = RE.Concat (RE.Concat x (RE.Loc "N")) x

    let dfa1 = RE.MakeDFA 1 (RE.Rev x)
    let dfa2 = RE.MakeDFA 2 (RE.Rev r2)
    
    let cg = ConstraintGraph.build (Topology.Example2.topo()) [|dfa1|] 
    
    ConstraintGraph.pruneHeuristic cg
    
    (* printfn "%s" (ConstraintGraph.toDot cg) *)

    (* ConstraintGraph.compile cg *)

    0


