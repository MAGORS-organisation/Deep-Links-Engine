// Global usings for the default PostgreSQL analytics provider.
//
// The Dle.Domain namespaces are imported project wide for the same reason they are imported
// project wide inside Dle.Domain itself: this assembly is a set of adapters for a small number of
// ports, and repeating the same three using directives in every file adds noise without adding
// information. InvariantCulture is imported because every format and parse in the solution must
// name it explicitly.

global using System.Globalization;

global using Dle.Domain.Analytics;
global using Dle.Domain.Attribution;
global using Dle.Domain.Ports;
