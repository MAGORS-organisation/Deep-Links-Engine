using Dle.IntegrationTests.Infrastructure;

using Xunit.Sdk;
using Xunit.v3;

// One PostgreSQL 18 and one Valkey 8 for the whole assembly (§D.4). The fixture applies the
// InitialSchema migration to a template database; each test copies it.
[assembly: AssemblyFixture(typeof(DleInfrastructureFixture))]

// Serial. These tests share two containers, two of them take a database server away on purpose to
// assert the chaos behaviour of §D.6, and several assert on process-wide state — the bounded click
// event channel, its drop counter, and the shadow ban budget. Running them in parallel would make
// each of those a coin toss.
[assembly: Parallelization(Mode = ParallelMode.None)]
