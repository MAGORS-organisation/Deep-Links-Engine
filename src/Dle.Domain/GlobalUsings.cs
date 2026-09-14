// Global usings for the shared kernel.
//
// The BCL namespaces below are the ones that appear in the binding contract itself:
// ReadOnlyDictionary<,>.Empty as the default for every dictionary property, InvariantCulture for
// every parse and format, and System.Text.Json for the serialization section.
//
// The Dle.Domain.* namespaces are imported project wide on purpose. The kernel is one contract cut
// into folders, not a set of layers, and the folders reference each other freely: a routing rule
// speaks about a ClientContext, a link snapshot about a ConsentMode, the serialization context
// about all of them. Importing them here keeps that from turning into a wall of using directives
// in every file and removes a whole class of cross slice compile errors.

global using System.Collections.ObjectModel;
global using System.Globalization;
global using System.Text.Json;
global using System.Text.Json.Serialization;

global using Dle.Domain.Abuse;
global using Dle.Domain.Analytics;
global using Dle.Domain.Attribution;
global using Dle.Domain.Clients;
global using Dle.Domain.Contracts;
global using Dle.Domain.Crypto;
global using Dle.Domain.Entities;
global using Dle.Domain.Links;
global using Dle.Domain.Ports;
global using Dle.Domain.Primitives;
global using Dle.Domain.Privacy;
global using Dle.Domain.Routing;
global using Dle.Domain.Serialization;
global using Dle.Domain.WellKnown;
