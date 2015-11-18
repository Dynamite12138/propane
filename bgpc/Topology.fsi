﻿module Topology
open QuickGraph

type NodeType = 
    | Start
    | End
    | Outside
    | Inside 
    | InsideOriginates

type State = 
    {Loc: string; 
     Typ: NodeType}

type T = BidirectionalGraph<State,TaggedEdge<State,unit>>

/// Build the internal and external alphabet from a topology
val alphabet: T -> Set<State> * Set<State> 

/// Check if a node is a valid topology node
val isTopoNode: State -> bool

/// Check if a node represents an internal location (under AS control)
val isInside: State -> bool

/// Check if a node can originate traffice (e.g., TOR in DC)
val canOriginateTraffic: State -> bool

/// Checks if a topology is well-formed. This involves checking 
/// for duplicate names, as well as checking that the inside is fully connected
val isWellFormed: State -> bool

/// Helper function for building topology
val addVertices: T -> State list -> unit 

/// Helper function for building topology
val addEdgesDirected: T -> (State*State) list -> unit

/// Helper function for building topology
val addEdgesUndirected: T -> (State*State) list -> unit