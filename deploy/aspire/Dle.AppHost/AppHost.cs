using Aspire.Hosting.ApplicationModel;

// .NET Aspire app host for local development (docs/zadanie.md §C.1, §C.2). One command brings up the
// backing services, both processes and the dashboard:
//
//     aspire run --project deploy/aspire/Dle.AppHost
//
// This file exists only to describe the local topology. It is not a deployment mechanism: production
// runs the two container images from deploy/ with their own configuration, so nothing here may be the
// only place a required setting is written down.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Persistent rather than session-scoped, and on a named volume, so that the schema a developer
// migrated and the links they created survive stopping the app host. A throwaway database would mean
// re-running the migration and re-seeding before every debugging session.
IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume("dle-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

// The resource name is the connection string name only when WithReference does not override it; the
// database itself is "dle", matching the migration and the compose file in deploy/.
IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("dle-postgres", "dle");

// Valkey rather than Redis (§C.2). Aspire ships no Valkey-specific resource, and none is needed: the
// wire protocol and the health check are the same, so the Redis resource with the Valkey image is the
// whole difference. Pinned to the major tag rather than latest so that a rebuild months from now
// still starts the container a developer last tested against.
IResourceBuilder<RedisResource> valkey = builder
    .AddRedis("valkey")
    .WithImage("valkey/valkey", "8")
    .WithLifetime(ContainerLifetime.Persistent);

// SHARED-KERNEL §16 names the connection strings Postgres and Valkey, and both processes read exactly
// those keys. connectionName pins that contract here rather than forcing the resource names to double
// as configuration keys.
//
// The control plane owns the schema, the workers and the administrative API. It waits for the
// database because its first action on a cold start is to check the migration state.
builder
    .AddProject<Projects.Dle_Control>("dle-control")
    .WithReference(database, connectionName: "Postgres")
    .WithReference(valkey, connectionName: "Valkey")
    .WaitFor(database)
    .WaitFor(valkey)
    .WithHttpHealthCheck("/healthz")
    .WithExternalHttpEndpoints();

// The data plane. It deliberately does not wait for the control plane: §D.6 requires the edge to
// start and serve from cache when its dependencies are unavailable, and a start-up ordering
// constraint here would quietly hide a regression in that behaviour.
builder
    .AddProject<Projects.Dle_Edge>("dle-edge")
    .WithReference(database, connectionName: "Postgres")
    .WithReference(valkey, connectionName: "Valkey")
    .WaitFor(database)
    .WaitFor(valkey)
    .WithHttpHealthCheck("/healthz")
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
