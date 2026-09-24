// Global usings for the optional ClickHouse analytics provider (ADR-006).
//
// The shared kernel namespaces are imported project wide for the same reason they are imported
// project wide inside Dle.Domain itself: this assembly is one adapter for one port, and repeating
// the same four using directives in every file adds noise without adding information.
//
// ClickHouse.Client is deliberately NOT imported here. This project's own namespace ends in
// "ClickHouse", so an unqualified reference to the package namespace inside the namespace body
// would be ambiguous; every file that needs the driver imports it explicitly at file scope.

global using System.Globalization;

global using Dle.Domain.Analytics;
global using Dle.Domain.Ports;
