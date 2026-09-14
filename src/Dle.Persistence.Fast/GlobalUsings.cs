// Global usings for the hot-path persistence module.
//
// Only namespaces that genuinely appear in almost every file live here: the invariant culture that
// SHARED-KERNEL §0 mandates for every parse and format, the Npgsql surface every query and every
// COPY writer touches, and the shared-kernel folders this module implements ports from.

global using System.Collections.ObjectModel;
global using System.Globalization;
global using System.Text.Json;

global using Dle.Domain.Analytics;
global using Dle.Domain.Links;
global using Dle.Domain.Ports;
global using Dle.Domain.Primitives;
global using Dle.Domain.Privacy;
global using Dle.Domain.Serialization;
global using Dle.Domain.WellKnown;

global using Npgsql;

global using NpgsqlTypes;
