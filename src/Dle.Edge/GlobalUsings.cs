// Global usings for the edge data plane.
//
// Only namespaces that genuinely appear in nearly every file live here: the invariant culture that
// SHARED-KERNEL §0 mandates for every parse and format, and the shared-kernel folders whose types
// the resolve pipeline speaks in from end to end — a request is classified into a ClientContext,
// gated into a ConsentDecision, routed into a RoutingDecision and recorded as a ClickEvent.

global using System.Globalization;

global using Dle.Domain.Analytics;
global using Dle.Domain.Clients;
global using Dle.Domain.Contracts;
global using Dle.Domain.Crypto;
global using Dle.Domain.Links;
global using Dle.Domain.Ports;
global using Dle.Domain.Primitives;
global using Dle.Domain.Privacy;
global using Dle.Domain.Routing;
