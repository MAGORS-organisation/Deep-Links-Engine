// Global usings for the default PostgreSQL analytics provider (ADR-006).
//
// The shared kernel namespaces are imported project wide for the same reason they are imported
// project wide inside Dle.Domain itself: this assembly is a set of adapters for a small number of
// ports, and repeating the same three using directives in every file adds noise without adding
// information. InvariantCulture is imported because SHARED-KERNEL §0 requires every format and
// parse in the solution to name it explicitly.

global using System.Globalization;

global using Dle.Domain.Analytics;
global using Dle.Domain.Attribution;
global using Dle.Domain.Ports;
