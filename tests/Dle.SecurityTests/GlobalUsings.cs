// Global usings for the security tests.
//
// These tests are adversarial: they build hostile inputs, drive the two hosts with them and assert on
// what came back. The namespaces below are the ones that appear in almost every file — the shared
// kernel's policies and contracts, the harness, and the assertion framework.

global using System.Globalization;
global using System.Net;
global using System.Net.Http;

global using Dle.Domain.Abuse;
global using Dle.Domain.Clients;
global using Dle.Domain.Links;
global using Dle.Domain.Ports;
global using Dle.Domain.Primitives;
global using Dle.Domain.Privacy;
global using Dle.Domain.Routing;
global using Dle.Domain.Serialization;
global using Dle.SecurityTests.Infrastructure;

global using Xunit;
