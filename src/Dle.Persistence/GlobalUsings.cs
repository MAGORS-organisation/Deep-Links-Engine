// Global usings for the control-plane persistence project.
//
// Dle.Domain.Entities.AppDomain collides with System.AppDomain, which ImplicitUsings imports
// everywhere. The alias below resolves it once, project wide, instead of forcing every file that
// touches the join table to write the fully qualified name.

global using System.Globalization;
global using Dle.Domain.Entities;
global using Dle.Persistence.Tenancy;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.EntityFrameworkCore.Metadata.Builders;
global using AppDomainEntity = Dle.Domain.Entities.AppDomain;
