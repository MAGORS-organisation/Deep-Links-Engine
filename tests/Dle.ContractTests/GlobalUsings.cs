// Global usings for the contract tests.
//
// Everything a contract test needs is either the assertion framework, the JSON reader it inspects
// the published document with, or the shared kernel's own contract namespaces — the DTOs whose wire
// shape is the thing under test. Importing them here keeps each test file about the contract it
// guards rather than about its own preamble.

global using System.Globalization;
global using System.Text.Json;
global using System.Text.Json.Nodes;

global using Dle.ContractTests.Infrastructure;
global using Dle.Domain.Contracts;
global using Dle.Domain.Serialization;

global using Xunit;
