// Global usings for the control plane.
//
// The three groups below are the ones that appear in practically every file of this project:
// the shared kernel (which is one contract cut into folders, so its namespaces travel together),
// the persistence module the control plane is written against, and the two BCL namespaces the
// coding standard mandates — InvariantCulture for every parse and format (SHARED-KERNEL §0) and
// System.Text.Json for the source-generated serialization contexts.
//
// Dle.Domain.Entities.AppDomain collides with System.AppDomain, which ImplicitUsings imports
// everywhere; the alias resolves it once for the whole project instead of in every file that
// touches the join table.

global using System.Globalization;
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
global using Dle.Persistence;
global using Dle.Persistence.Repositories;
global using Dle.Persistence.Tenancy;
global using AppDomainEntity = Dle.Domain.Entities.AppDomain;
global using AppEntity = Dle.Domain.Entities.App;
// Dle.Domain.Attribution.MatchType collides with System.IO.MatchType, which ImplicitUsings imports
// everywhere. The alias keeps the plain name meaning the domain enumeration across the project,
// exactly as AppDomainEntity does for the join table above.
global using MatchType = Dle.Domain.Attribution.MatchType;
