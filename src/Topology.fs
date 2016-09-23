﻿module Topology

open FSharp.Data
open QuickGraph
open QuickGraph.Algorithms
open Route
open System.Collections.Generic
open System.Xml
open Util.Error
open Util.Format

type NodeType = 
   | Start
   | End
   | Outside
   | Inside
   | Unknown of Set<string>

type Node = 
   struct
      val Loc : string
      val Typ : NodeType
      new(l, t) = 
         { Loc = l
           Typ = t }
   end

type T = 
   | Topology of BidirectionalGraph<Node, Edge<Node>>

let copyTopology (Topology(topo) : T) : T = 
   let newTopo = BidirectionalGraph<Node, Edge<Node>>()
   for v in topo.Vertices do
      newTopo.AddVertex v |> ignore
   for e in topo.Edges do
      newTopo.AddEdge e |> ignore
   Topology(newTopo)

let alphabet (Topology(topo) : T) : Set<Node> * Set<Node> = 
   let mutable ain = Set.empty
   let mutable aout = Set.empty
   for v in topo.Vertices do
      match v.Typ with
      | Inside -> ain <- Set.add v ain
      | Outside | Unknown _ -> aout <- Set.add v aout
      | Start | End -> failwith "unreachable"
   (ain, aout)

let vertices (Topology(topo) : T) = topo.Vertices
let neighbors (Topology(topo) : T) v = topo.OutEdges v |> Seq.map (fun e -> e.Target)
let edges (Topology(topo) : T) = topo.Edges |> Seq.map (fun e -> (e.Source, e.Target))
let inEdges (Topology(topo) : T) v = topo.InEdges v |> Seq.map (fun e -> (e.Source, e.Target))
let outEdges (Topology(topo) : T) v = topo.OutEdges v |> Seq.map (fun e -> (e.Source, e.Target))

let isTopoNode (t : Node) = 
   match t.Typ with
   | Start | End -> false
   | _ -> true

let isOutside (t : Node) = 
   match t.Typ with
   | Outside -> true
   | Unknown _ -> true
   | _ -> false

let isInside (t : Node) = 
   match t.Typ with
   | Inside -> true
   | _ -> false

let isUnknown (t : Node) = 
   match t.Typ with
   | Unknown _ -> true
   | _ -> false

let canOriginateTraffic (t : Node) = 
   match t.Typ with
   | Outside -> true
   | Unknown _ -> true
   | Inside -> true
   | Start | End -> false

let isWellFormed (topo : T) : bool = 
   let (Topology(onlyInside)) = copyTopology topo
   onlyInside.RemoveVertexIf(fun v -> isOutside v) |> ignore
   let d = Dictionary<Node, int>()
   ignore (onlyInside.WeaklyConnectedComponents d)
   (Set.ofSeq d.Values).Count = 1

let rec addVertices (topo : T) (vs : Node list) = 
   let (Topology(t)) = topo
   match vs with
   | [] -> ()
   | v :: vs -> 
      t.AddVertex v |> ignore
      addVertices topo vs

let rec addEdgesUndirected (topo : T) (es : (Node * Node) list) = 
   let (Topology(t)) = topo
   match es with
   | [] -> ()
   | (x, y) :: es -> 
      let e1 = Edge(x, y)
      let e2 = Edge(y, x)
      ignore (t.AddEdge e1)
      ignore (t.AddEdge e2)
      addEdgesUndirected topo es

let rec addEdgesDirected (topo : T) (es : (Node * Node) list) = 
   let (Topology(t)) = topo
   match es with
   | [] -> ()
   | (x, y) :: es -> 
      let e = Edge(x, y)
      ignore (t.AddEdge e)
      addEdgesDirected topo es

let findByLoc (Topology(topo) : T) loc = topo.Vertices |> Seq.tryFind (fun v -> v.Loc = loc)
let peers (Topology(topo) : T) (node : Node) = topo.OutEdges node |> Seq.map (fun e -> e.Target)

let findLinks (topo : T) (froms, tos) = 
   let (Topology(t)) = topo
   let mutable pairs = []
   for x in Set.toSeq froms do
      for y in Set.toSeq tos do
         let a = findByLoc topo x
         let b = findByLoc topo y
         match a, b with
         | Some a, Some b -> 
            let ns = 
               t.OutEdges a
               |> Seq.map (fun (e : Edge<Node>) -> e.Target)
               |> Set.ofSeq
            if Set.contains b ns then pairs <- (a, b) :: pairs
         | _, _ -> ()
   pairs

type Topo = XmlProvider< "../data/template.xml" >

type Kind = 
   | Concrete
   | Abstract
   | Template

type GraphInfo = 
   { Graph : T
     InternalNames : Set<string>
     ExternalNames : Set<string>
     AsnMap : Map<string, int>
     IpMap : Dictionary<string * string, string * string> }

type TopoInfo = 
   class
      val NetworkAsn : int
      val Kind : Kind
      val ConcreteGraphInfo : GraphInfo
      val AbstractGraphInfo : GraphInfo
      val EdgeLabels : Map<string * string, string>
      val NodeLabels : Map<string, string>
      val Constraints : List<string>
      
      new(nasn, k, cg, ag, els, nls, cs) = 
         { NetworkAsn = nasn
           Kind = k
           ConcreteGraphInfo = cg
           AbstractGraphInfo = ag
           EdgeLabels = els
           NodeLabels = nls
           Constraints = cs }
      
      member this.SelectGraphInfo = 
         match this.Kind with
         | Concrete -> this.ConcreteGraphInfo
         | Abstract | Template -> this.AbstractGraphInfo
      
      member this.IsTemplate = 
         match this.Kind with
         | Concrete -> false
         | Abstract -> false
         | Template -> true
   end

let MAX_ASN = 65534
let counter = ref 0
let currASN = ref (MAX_ASN + 1)

let parseAsn s = 
   if s = "" then error (sprintf "Invalid topology: No asn specified")
   try 
      int s
   with _ -> error (sprintf "Invalid topology: Unrecognized AS number %s" s)

let getAsn isAbstract inside name (asn : string) = 
   if isAbstract then 
      if asn <> "" then 
         error (sprintf "Invalid topology: ASN included in abstract topology: %s" asn)
      else 
         incr counter
         !counter
   else if asn = "" then 
      if not inside then error (sprintf "Invalid topology: ASN required for external peer: %s" name)
      else 
         decr currASN
         !currASN
   else 
      let i = parseAsn asn
      if i < 0 then error (sprintf "Negative AS number '%s' in topology for node '%s'" asn name)
      else i

let router (asn : string) (ti : TopoInfo) = 
   let inline eqAsn _ v = string v = asn
   match Map.tryFindKey eqAsn ti.SelectGraphInfo.AsnMap with
   | None -> 
      if asn = "out" then asn
      else "AS" + asn
   | Some r -> r

let assignIps sip tip ip = 
   match (sip, tip) with
   | "", "" -> 
      let (_, _, c, d) = Route.Bitwise.toDotted !ip
      let s = sprintf "10.%d.%d.1" c d
      let t = sprintf "10.%d.%d.2" c d
      incr ip
      (s, t)
   | _, "" | "", _ -> error "Missing source or target ip in topology"
   | _, _ -> (sip, tip)

let addEdge (g : BidirectionalGraph<Node, Edge<Node>>) (seen : HashSet<_>) x y = 
   if not (seen.Contains(x, y)) then 
      seen.Add(x, y) |> ignore
      let e = Edge(x, y)
      ignore (g.AddEdge e)

let addForGraph (asnMap, nameMap, nodeMap, internalNames, externalNames, 
                 g : BidirectionalGraph<Node, Edge<Node>>) isAbstract intern name asn = 
   let asn = getAsn isAbstract intern name asn
   match Map.tryFind name !asnMap with
   | None -> 
      asnMap := Map.add name asn !asnMap
      match Map.tryFind asn !nameMap with
      | Some e -> error (sprintf "Duplicate AS numbers for %s and %s" e name)
      | None -> ()
      nameMap := Map.add asn name !nameMap
   | Some _ -> error (sprintf "Duplicate name '%s' in topology" name)
   let typ = 
      if intern then Inside
      else Outside
   if intern then internalNames := Set.add name !internalNames
   else externalNames := Set.add name !externalNames
   let state = Node(string asn, typ)
   nodeMap := Map.add name state !nodeMap
   ignore (g.AddVertex state)

let readTopology (file : string) : TopoInfo = 
   let settings = Args.getSettings()
   try 
      let concreteSeen = HashSet() // avoid adding edges twice
      let abstractSeen = HashSet()
      let topo = Topo.Load file
      let concreteG = BidirectionalGraph<Node, Edge<Node>>()
      let abstractG = BidirectionalGraph<Node, Edge<Node>>()
      let concreteNodeMap = ref Map.empty
      let abstractNodeMap = ref Map.empty
      let concreteAsnMap = ref Map.empty
      let abstractAsnMap = ref Map.empty
      let concreteInternalNames = ref Set.empty
      let abstractInternalNames = ref Set.empty
      let concreteExternalNames = ref Set.empty
      let abstractExternalNames = ref Set.empty
      let concreteNameMap = ref Map.empty
      let abstractNameMap = ref Map.empty
      let concreteIpMap = Dictionary()
      let abstractIpMap = Dictionary()
      let abstractNodeLabels = ref Map.empty
      let abstractEdgeLabels = ref Map.empty
      let currASN = ref MAX_ASN
      // collect constraints
      let constraints = List()
      for c in topo.Constraints do
         constraints.Add c.Assertion
      // Build the concretization map and determine if we are using an abstract topology
      let isPureAbstract = (topo.Nodes.Length = 0)
      let mutable isAbstract = isPureAbstract
      let concretization = ref Map.empty
      for an in topo.Abstractnodes do
         concretization := Map.add an.Label Set.empty !concretization
      for n in topo.Nodes do
         if n.Group <> "" then 
            isAbstract <- true
            match Map.tryFind n.Group !concretization with
            | None -> 
               error (sprintf "Concrete node: %s refers to non-existant group %s" n.Name n.Group)
            | Some s -> 
               let s' = Set.add n.Group s
               concretization := Map.add n.Group s' !concretization
      // Add nodes to the concrete graph
      for n in topo.Nodes do
         let args = 
            (concreteAsnMap, concreteNameMap, concreteNodeMap, concreteInternalNames, 
             concreteExternalNames, concreteG)
         addForGraph args isAbstract n.Internal n.Name n.Asn
      // Addo nodes to the abstract graph
      if isAbstract then 
         for n in topo.Abstractnodes do
            if n.Label = "" then error (sprintf "Invalid abstract node label - found empty string")
            abstractNodeLabels := Map.add n.Label n.Label !abstractNodeLabels
            let args = 
               (abstractAsnMap, abstractNameMap, abstractNodeMap, abstractInternalNames, 
                abstractExternalNames, abstractG)
            addForGraph args isAbstract n.Internal n.Label ""
      // Perform IP address asssignment and add edges to graph
      let ip = ref 0
      for e in topo.Edges do
         if not ((!concreteNodeMap).ContainsKey e.Source) then 
            error (sprintf "Invalid edge source location %s in topology" e.Source)
         elif not ((!concreteNodeMap).ContainsKey e.Target) then 
            error (sprintf "Invalid edge target location %s in topology" e.Target)
         else 
            let x = (!concreteNodeMap).[e.Source]
            let y = (!concreteNodeMap).[e.Target]
            addEdge concreteG concreteSeen x y
            addEdge concreteG concreteSeen y x
            let (s, t) = assignIps e.SourceIp e.TargetIp ip
            concreteIpMap.[(x.Loc, y.Loc)] <- (s, t)
            concreteIpMap.[(y.Loc, x.Loc)] <- (t, s)
      // Add abstract edges if applicable
      if isAbstract then 
         for e in topo.Abstractedges do
            if not ((!abstractNodeMap).ContainsKey e.Source) then 
               error (sprintf "Invalid abstract edge source location %s in topology" e.Source)
            elif not ((!abstractNodeMap).ContainsKey e.Target) then 
               error (sprintf "Invalid abstract edge target location %s in topology" e.Target)
            else 
               if e.SourceLabel = "" then 
                  let msg = 
                     sprintf "Missing abstract edge source label for edge: (%s,%s)" e.Source 
                        e.Target
                  error msg
               if e.TargetLabel = "" then 
                  let msg = 
                     sprintf "Missing abstract edge target label for edge: (%s,%s)" e.Source 
                        e.Target
                  error msg
               abstractEdgeLabels := Map.add (e.Source, e.Target) e.SourceLabel !abstractEdgeLabels
               abstractEdgeLabels := Map.add (e.Target, e.Source) e.TargetLabel !abstractEdgeLabels
               let x = (!abstractNodeMap).[e.Source]
               let y = (!abstractNodeMap).[e.Target]
               addEdge abstractG abstractSeen x y
               addEdge abstractG abstractSeen y x
         // Check for duplicate labels
         let ves = Map.toSeq !abstractEdgeLabels |> Seq.map snd
         let vns = Map.toSeq !abstractNodeLabels |> Seq.map snd
         let all = Seq.append ves vns
         let bySize = Seq.groupBy id all
         for (label, vs) in bySize do
            if Seq.length vs > 1 then 
               let msg = sprintf "Duplicate label (%s) found in the topology. " label
               error msg
      let kind = 
         if isPureAbstract then Template
         else if isAbstract then Abstract
         else Concrete
      // Warn if using a template that certain checks can't be done
      if isPureAbstract then 
         let msg = 
            sprintf 
               "The topology provided is purely abstract. This means that the safety checks will only hold " 
            + sprintf "if the concrete topology is a true instantiation of the abstract topology."
         warning msg
      // Update the settings so we know if we are compiling an abstract topology
      Args.changeSettings { settings with IsAbstract = isAbstract }
      let concreteGI = 
         { Graph = Topology(concreteG)
           InternalNames = !concreteInternalNames
           ExternalNames = !concreteExternalNames
           AsnMap = !concreteAsnMap
           IpMap = concreteIpMap }
      
      let abstractGI = 
         { Graph = Topology(abstractG)
           InternalNames = !abstractInternalNames
           ExternalNames = !abstractExternalNames
           AsnMap = !abstractAsnMap
           IpMap = abstractIpMap }
      
      (* printfn "concreteNodeMap: %A" !concreteNodeMap
      printfn "abstractNodeMap: %A" !abstractNodeMap
      printfn "concreteAsnMap: %A" !concreteAsnMap
      printfn "abstractAsnMap: %A" !abstractAsnMap
      printfn "concreteInternalNames: %A" !concreteInternalNames
      printfn "abstractInternalNames: %A" !abstractInternalNames
      printfn "concreteExternalNames: %A" !concreteExternalNames
      printfn "abstractExternalNames: %A" !abstractExternalNames
      printfn "concreteNameMap: %A" !concreteNameMap
      printfn "abstractNameMap: %A" !abstractNameMap
      printfn "concreteIpMap: %A" concreteIpMap
      printfn "abstractIpMap: %A" abstractIpMap *)
      let netAsn = parseAsn (topo.Asn)
      TopoInfo
         (netAsn, kind, concreteGI, abstractGI, !abstractEdgeLabels, !abstractNodeLabels, 
          constraints)
   with _ -> error (sprintf "Invalid topology XML file")

module Examples = 
   let topoDisconnected() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      addVertices g [ vA; vB; vC; vD ]
      addEdgesUndirected g [ (vA, vB)
                             (vC, vD) ]
      g
   
   let topoDiamond() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vX = Node("X", Inside)
      let vM = Node("M", Inside)
      let vN = Node("N", Inside)
      let vY = Node("Y", Inside)
      let vZ = Node("Z", Inside)
      let vB = Node("B", Inside)
      addVertices g [ vA; vX; vM; vN; vY; vZ; vB ]
      addEdgesUndirected g [ (vA, vX)
                             (vA, vM)
                             (vM, vN)
                             (vX, vN)
                             (vN, vY)
                             (vN, vZ)
                             (vY, vB)
                             (vZ, vB) ]
      g
   
   let topoDatacenterSmall() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vX = Node("X", Inside)
      let vY = Node("Y", Inside)
      let vM = Node("M", Inside)
      let vN = Node("N", Inside)
      addVertices g [ vA; vB; vC; vD; vX; vY; vM; vN ]
      addEdgesUndirected g [ (vA, vX)
                             (vB, vX)
                             (vC, vY)
                             (vD, vY)
                             (vX, vM)
                             (vX, vN)
                             (vY, vM)
                             (vY, vN) ]
      g
   
   let topoDatacenterMedium() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      let vF = Node("F", Inside)
      let vG = Node("G", Inside)
      let vH = Node("H", Inside)
      let vX = Node("X", Inside)
      let vY = Node("Y", Inside)
      addVertices g [ vA; vB; vC; vD; vE; vF; vG; vH; vX; vY ]
      addEdgesUndirected g [ (vA, vC)
                             (vA, vD)
                             (vB, vC)
                             (vB, vD)
                             (vE, vG)
                             (vE, vH)
                             (vF, vG)
                             (vF, vH)
                             (vC, vX)
                             (vC, vY)
                             (vD, vX)
                             (vD, vY)
                             (vG, vX)
                             (vG, vY)
                             (vH, vX)
                             (vH, vY) ]
      g
   
   let topoDatacenterMediumAggregation() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      let vF = Node("F", Inside)
      let vG = Node("G", Inside)
      let vH = Node("H", Inside)
      let vX = Node("X", Inside)
      let vY = Node("Y", Inside)
      let vPeer = Node("PEER", Outside)
      addVertices g [ vA; vB; vC; vD; vE; vF; vG; vH; vX; vY; vPeer ]
      addEdgesUndirected g [ (vA, vC)
                             (vA, vD)
                             (vB, vC)
                             (vB, vD)
                             (vE, vG)
                             (vE, vH)
                             (vF, vG)
                             (vF, vH)
                             (vC, vX)
                             (vC, vY)
                             (vD, vX)
                             (vD, vY)
                             (vG, vX)
                             (vG, vY)
                             (vH, vX)
                             (vH, vY)
                             (vX, vPeer)
                             (vY, vPeer) ]
      g
   
   let topoDatacenterLarge() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      let vF = Node("F", Inside)
      let vM = Node("M", Inside)
      let vN = Node("N", Inside)
      let vO = Node("O", Inside)
      let vX = Node("X", Inside)
      let vY = Node("Y", Inside)
      let vZ = Node("Z", Inside)
      addVertices g [ vA; vB; vC; vD; vE; vF; vM; vN; vO; vX; vY; vZ ]
      addEdgesUndirected g [ (vA, vX)
                             (vB, vX)
                             (vC, vY)
                             (vD, vY)
                             (vE, vZ)
                             (vF, vZ)
                             (vX, vM)
                             (vX, vN)
                             (vX, vO)
                             (vY, vM)
                             (vY, vN)
                             (vY, vO)
                             (vZ, vM)
                             (vZ, vN)
                             (vZ, vO) ]
      g
   
   let topoBadGadget() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      addVertices g [ vA; vB; vC; vD ]
      addEdgesUndirected g [ (vA, vB)
                             (vB, vC)
                             (vC, vA)
                             (vA, vD)
                             (vB, vD)
                             (vC, vD) ]
      g
   
   let topoBrokenTriangle() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      addVertices g [ vA; vB; vC; vD; vE ]
      addEdgesUndirected g [ (vC, vA)
                             (vA, vE)
                             (vA, vB)
                             (vE, vD)
                             (vD, vB) ]
      g
   
   let topoBigDipper() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      addVertices g [ vA; vC; vD; vE ]
      addEdgesUndirected g [ (vC, vA)
                             (vA, vE)
                             (vA, vD)
                             (vE, vD) ]
      g
   
   let topoSeesaw() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vM = Node("M", Inside)
      let vN = Node("N", Inside)
      let vO = Node("O", Inside)
      let vX = Node("X", Inside)
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      addVertices g [ vM; vN; vO; vX; vA; vB ]
      addEdgesUndirected g [ (vM, vN)
                             (vM, vO)
                             (vO, vX)
                             (vN, vX)
                             (vX, vA)
                             (vX, vB) ]
      g
   
   let topoStretchingManWAN() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vX = Node("X", Outside)
      let vY = Node("Y", Outside)
      let vZ = Node("Z", Outside)
      addVertices g [ vA; vB; vC; vD; vX; vY; vZ ]
      addEdgesUndirected g [ (vX, vA)
                             (vX, vB)
                             (vA, vC)
                             (vB, vC)
                             (vC, vD)
                             (vD, vY)
                             (vD, vZ) ]
      g
   
   let topoStretchingManWAN2() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      let vW = Node("W", Outside)
      let vX = Node("X", Outside)
      let vY = Node("Y", Outside)
      let vZ = Node("Z", Outside)
      addVertices g [ vA; vB; vC; vD; vE; vW; vX; vY; vZ ]
      addEdgesUndirected g [ (vW, vA)
                             (vW, vB)
                             (vA, vC)
                             (vB, vC)
                             (vC, vD)
                             (vC, vE)
                             (vD, vX)
                             (vD, vY)
                             (vE, vZ) ]
      g
   
   let topoPinCushionWAN() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vB = Node("B", Inside)
      let vC = Node("C", Inside)
      let vD = Node("D", Inside)
      let vE = Node("E", Inside)
      let vW = Node("W", Outside)
      let vX = Node("X", Outside)
      let vY = Node("Y", Outside)
      let vZ = Node("Z", Outside)
      addVertices g [ vA; vB; vC; vD; vE; vW; vX; vY; vZ ]
      addEdgesUndirected g [ (vW, vA)
                             (vX, vB)
                             (vA, vC)
                             (vB, vC)
                             (vC, vD)
                             (vC, vE)
                             (vD, vY)
                             (vE, vZ) ]
      g
   
   let topoBackboneWAN() = 
      let g = Topology(BidirectionalGraph<Node, Edge<Node>>())
      let vA = Node("A", Inside)
      let vSEA = Node("SEA", Inside)
      let vNY = Node("NY", Inside)
      let vX = Node("X", Outside)
      let vY = Node("Y", Outside)
      addVertices g [ vA; vSEA; vNY; vX; vY ]
      addEdgesUndirected g [ (vA, vSEA)
                             (vA, vNY)
                             (vSEA, vX)
                             (vNY, vY) ]
      g
   
   /// Fattree topology 
   type Tiers = Dictionary<Node, int>
   
   type Prefixes = Dictionary<Node, Prefix>
   
   let getPrefix i = 
      let a = (i / (256 * 256))
      let b = (i / 256)
      let c = (i % 256)
      Prefix(a, b, c, 0, 24)
   
   let fatTree k : T * Prefixes * Tiers = 
      let iT0 = (k * k) / 2
      let iT1 = (k * k) / 2
      let iT2 = (k * k) / 4
      let g = BidirectionalGraph<Node, Edge<Node>>()
      let prefixes = Dictionary()
      let tiers = Dictionary()
      
      let routersT0 = 
         Array.init iT0 (fun i -> 
            let name = "T0_" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            prefixes.[v] <- getPrefix i
            tiers.[v] <- 0
            v)
      
      let routersT1 = 
         Array.init iT1 (fun i -> 
            let name = "T1_" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            tiers.[v] <- 1
            v)
      
      let routersT2 = 
         Array.init iT2 (fun i -> 
            let name = "T2_" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            tiers.[v] <- 2
            v)
      
      let t = Topology(g)
      let perPod = (k / 2)
      for i = 0 to iT0 - 1 do
         let pod = i / (perPod)
         for j = 0 to perPod - 1 do
            let x = routersT0.[i]
            let y = routersT1.[pod * perPod + j]
            addEdgesUndirected t [ (x, y) ]
      for i = 0 to iT1 - 1 do
         for j = 0 to perPod - 1 do
            let rem = i % perPod
            let x = routersT1.[i]
            let y = routersT2.[rem * perPod + j]
            addEdgesUndirected t [ (x, y) ]
      let back1 = Node("BACK1", Outside)
      let back2 = Node("BACK2", Outside)
      ignore (g.AddVertex back1)
      ignore (g.AddVertex back2)
      for i = 0 to iT2 - 1 do
         let x = routersT2.[i]
         addEdgesUndirected t [ (x, back1)
                                (x, back2) ]
      (t, prefixes, tiers)
   
   let complete n = 
      let g = BidirectionalGraph<Node, Edge<Node>>()
      // setup external peers
      let externalPeers = Dictionary()
      let internalPeers = Dictionary()
      // setup internal full mesh
      for i = 0 to n - 1 do
         let name = "R" + string i
         let v = Node(name, Inside)
         ignore (g.AddVertex v)
         internalPeers.[name] <- v
      for v1 in g.Vertices do
         for v2 in g.Vertices do
            if isInside v1 && isInside v2 && v1 <> v2 then 
               let e = Edge(v1, v2)
               ignore (g.AddEdge e)
      // add connections to external peers
      for kv in internalPeers do
         let name = kv.Key
         let router = kv.Value
         // add dcs
         for i = 0 to 9 do
            let eName = "Cust" + string i + name
            let ePeer = Node(eName, Outside)
            ignore (g.AddVertex ePeer)
            let e1 = Edge(router, ePeer)
            let e2 = Edge(ePeer, router)
            ignore (g.AddEdge e1)
            ignore (g.AddEdge e2)
         // add peers
         for i = 0 to 19 do
            let eName = "Peer" + string i + name
            let ePeer = Node(eName, Outside)
            ignore (g.AddVertex ePeer)
            let e1 = Edge(router, ePeer)
            let e2 = Edge(ePeer, router)
            ignore (g.AddEdge e1)
            ignore (g.AddEdge e2)
         // add paid on net
         for i = 0 to 19 do
            let eName = "OnPaid" + string i + name
            let ePeer = Node(eName, Outside)
            ignore (g.AddVertex ePeer)
            let e1 = Edge(router, ePeer)
            let e2 = Edge(ePeer, router)
            ignore (g.AddEdge e1)
            ignore (g.AddEdge e2)
         // add paid off net
         for i = 0 to 19 do
            let eName = "OffPaid" + string i + name
            let ePeer = Node(eName, Outside)
            ignore (g.AddVertex ePeer)
            let e1 = Edge(router, ePeer)
            let e2 = Edge(ePeer, router)
            ignore (g.AddEdge e1)
            ignore (g.AddEdge e2)
      Topology(g)

module Test = 
   let testTopologyWellFormedness() = 
      printf "Topology well-formedness "
      let topo = Examples.topoDisconnected()
      if isWellFormed topo then failed()
      else passed()
   
   let run() = testTopologyWellFormedness()